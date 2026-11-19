using System.Security.Cryptography;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Tests.Fixtures;

namespace FileOrganizer.Tests;

/// <summary>
/// Fixed, privately minted test-session transport; children ingest approved metadata, never a source path.
/// Retained directory identities and handle-relative control reads narrow substitution opportunities.
/// SQLite still opens its database/WAL by absolute path: this bounded harness assumes no external
/// reparse metadata mutations of its private state and does not claim an adversarially race-free VFS.
/// </summary>
internal sealed class ProcessInventorySession : IDisposable
{
    private const int MaximumSnapshotBytes = 1024 * 1024;
    private readonly WindowsDirectoryPins pins = new();
    private readonly List<FileStream> retained = [];
    public string SessionId { get; }
    public string Root { get; }
    public AppDataPaths Paths { get; }
    public InventorySnapshot Snapshot { get; private set; } = null!;
    public string Fingerprint { get; private set; } = string.Empty;

    private ProcessInventorySession(string sessionId, string evidenceTask)
    {
        if (!Guid.TryParseExact(sessionId, "N", out var parsed) || parsed.ToString("N") != sessionId)
            throw new InvalidOperationException("fixture.session.invalid_id");
        if (evidenceTask is not ("FO-005-recovery-20261029" or "FO-005-streaming-20261029" or "FO-007-operation-journal-20261118"))
            throw new InvalidOperationException("fixture.session.invalid_task");
        SessionId = sessionId;
        var checkout = FindCheckout();
        Root = Path.Combine(checkout, "artifacts", evidenceTask, "process-data", sessionId);
        Paths = new AppDataPaths(Path.Combine(Root, "profile"));
        RejectReparseAncestors(Root);
    }

    public static ProcessInventorySession Create(InventorySnapshot snapshot, string evidenceTask = "FO-005-recovery-20261029")
    {
        var session = new ProcessInventorySession(Guid.NewGuid().ToString("N"), evidenceTask);
        try
        {
            var prepared = PreparedInventorySnapshot.Create(snapshot);
            Directory.CreateDirectory(session.Root);
            RejectReparseAncestors(session.Root);
            session.pins.PinAncestors(session.Root);
            session.PinProfileDirectories(create: true);
            session.ValidateProfileBoundary();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var envelope = new SnapshotEnvelope(session.SessionId, token, snapshot);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
            if (bytes.Length > MaximumSnapshotBytes) throw new InvalidOperationException("fixture.session.snapshot_limit");
            var marker = new SessionMarker("FileOrganizer.ProcessInventory.v1", session.SessionId, token,
                Convert.ToHexString(SHA256.HashData(bytes)), prepared.Fingerprint);
            session.CreateRetained("snapshot.json", bytes);
            session.CreateRetained("session.json", JsonSerializer.SerializeToUtf8Bytes(marker));
            session.Snapshot = snapshot;
            session.Fingerprint = prepared.Fingerprint;
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    public static ProcessInventorySession OpenChild(string sessionId, string evidenceTask = "FO-005-recovery-20261029")
    {
        var session = new ProcessInventorySession(sessionId, evidenceTask);
        try
        {
            if (!Directory.Exists(session.Root)) throw new InvalidOperationException("fixture.session.missing");
            session.pins.PinAncestors(session.Root);
            session.PinProfileDirectories(create: false);
            var markerBytes = session.ReadRetained("session.json", 4096);
            var snapshotBytes = session.ReadRetained("snapshot.json", MaximumSnapshotBytes);
            var marker = JsonSerializer.Deserialize<SessionMarker>(markerBytes) ?? throw new InvalidOperationException("fixture.session.marker");
            var envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(snapshotBytes, new JsonSerializerOptions { MaxDepth = 16 })
                ?? throw new InvalidOperationException("fixture.session.snapshot");
            if (marker.Format != "FileOrganizer.ProcessInventory.v1" || marker.SessionId != sessionId || envelope.SessionId != sessionId ||
                marker.Token.Length != 64 || marker.Token.Any(character => !Uri.IsHexDigit(character)) || marker.Token != envelope.Token ||
                marker.SnapshotSha256 != Convert.ToHexString(SHA256.HashData(snapshotBytes)))
                throw new InvalidOperationException("fixture.session.binding");
            var prepared = PreparedInventorySnapshot.Create(envelope.Snapshot);
            if (prepared.Fingerprint != marker.Fingerprint) throw new InvalidOperationException("fixture.session.fingerprint");
            session.ValidateProfileBoundary();
            session.Snapshot = envelope.Snapshot;
            session.Fingerprint = marker.Fingerprint;
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    // T-0057: same privately minted session and retained, bounded controls; no source path transport.
    public void SetReconciliationPlan(InventoryReconciliationPlan plan)
    {
        if (plan.Before.RootIdentity != Snapshot.RootIdentity)
            throw new InvalidOperationException("fixture.session.reconciliation_root");
        CreateRetained("reconciliation-plan.json", JsonSerializer.SerializeToUtf8Bytes(
            new ReconciliationEnvelope(SessionId, Fingerprint, plan)));
    }

    public InventoryReconciliationPlan ReadReconciliationPlan()
    {
        var envelope = JsonSerializer.Deserialize<ReconciliationEnvelope>(
            ReadRetained("reconciliation-plan.json", MaximumSnapshotBytes), new JsonSerializerOptions { MaxDepth = 16 })
            ?? throw new InvalidOperationException("fixture.session.reconciliation_plan");
        if (envelope.SessionId != SessionId || envelope.Fingerprint != Fingerprint ||
            envelope.Plan.Before.RootIdentity != Snapshot.RootIdentity)
            throw new InvalidOperationException("fixture.session.reconciliation_binding");
        return envelope.Plan;
    }

    public void ValidateProfileBoundary()
    {
        pins.Validate();
        foreach (var path in new[] { Paths.RootDirectory, Paths.DataDirectory, Paths.LogsDirectory, Paths.DatabasePath,
                     Paths.DatabasePath + "-wal", Paths.DatabasePath + "-shm", Path.Combine(Paths.RootDirectory, "profile.lock"),
                     Path.Combine(Paths.DataDirectory, "backups") })
            RejectReparseAncestors(path);
        // Only this minted profile's bounded migration backups are inspected; no source tree scan.
        var backups = Path.Combine(Paths.DataDirectory, "backups");
        var count = 0;
        foreach (var backup in Directory.EnumerateFiles(backups, "before-inventory-v*-*.db", SearchOption.TopDirectoryOnly))
        {
            if (++count > 4) throw new InvalidOperationException("fixture.session.backup_limit");
            var backupName = Path.GetFileName(backup);
            if (!backupName.StartsWith("before-inventory-v2-", StringComparison.Ordinal) &&
                !backupName.StartsWith("before-inventory-v3-", StringComparison.Ordinal) &&
                !backupName.StartsWith("before-inventory-v4-", StringComparison.Ordinal) &&
                !backupName.StartsWith("before-inventory-v5-", StringComparison.Ordinal))
                throw new InvalidOperationException("fixture.session.backup_version");
            RejectReparseAncestors(backup);
            using var handle = pins.OpenReadFile(backups, Path.GetFileName(backup));
        }
    }

    private void PinProfileDirectories(bool create)
    {
        var backups = Path.Combine(Paths.DataDirectory, "backups");
        if (create)
        {
            pins.CreateChildDirectory(Root, "profile", allowExisting: false);
            pins.CreateChildDirectory(Paths.RootDirectory, "data", allowExisting: false);
            pins.CreateChildDirectory(Paths.RootDirectory, "logs", allowExisting: false);
            pins.CreateChildDirectory(Paths.DataDirectory, "backups", allowExisting: false);
        }
        else
        {
            foreach (var directory in new[] { Paths.RootDirectory, Paths.DataDirectory, Paths.LogsDirectory, backups })
            {
                RejectReparseAncestors(directory);
                pins.PinAncestors(directory);
            }
        }
    }

    public void WriteReceipt(string name, byte[] bytes)
    {
        if (name is not ("pause-observation.json" or "killed-observation.json" or "resume-result.json" or "resume-observation.json" or "capture-killed.json" or "capture-resumed.json" or "capture-verified.json" or "native-benchmark.json") || bytes.Length > 8192)
            throw new InvalidOperationException("fixture.session.receipt");
        pins.Validate();
        using var handle = pins.OpenNewFile(Root, name);
        using var stream = new FileStream(handle, FileAccess.Write);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private void CreateRetained(string name, byte[] bytes)
    {
        using var handle = pins.OpenNewFile(Root, name);
        using (var output = new FileStream(handle, FileAccess.Write))
        {
            output.Write(bytes);
            output.Flush(true);
        }
        _ = ReadRetained(name, MaximumSnapshotBytes);
    }

    private byte[] ReadRetained(string name, int maximumBytes)
    {
        pins.Validate();
        var handle = pins.OpenReadFile(Root, name);
        FileStream stream;
        try { stream = new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
        retained.Add(stream);
        if (stream.Length is < 1 || stream.Length > maximumBytes) throw new InvalidOperationException("fixture.session.control_size");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void RejectReparseAncestors(string path)
    {
        var currentPath = Path.GetFullPath(path);
        while (true)
        {
            if ((File.Exists(currentPath) || Directory.Exists(currentPath)) && File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("fixture.session.reparse");
            var parent = Path.GetDirectoryName(currentPath);
            if (parent is null || parent == currentPath) return;
            currentPath = parent;
        }
    }

    internal static string FindCheckout()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "FileOrganizer.slnx")) &&
                File.Exists(Path.Combine(current.FullName, "tests", "FileOrganizer.Tests", "FileOrganizer.Tests.csproj"))) return current.FullName;
        throw new InvalidOperationException("fixture.session.checkout_missing");
    }

    public void Dispose()
    {
        foreach (var stream in retained) stream.Dispose();
        pins.Dispose();
    }

    private sealed record ReconciliationEnvelope(string SessionId, string Fingerprint, InventoryReconciliationPlan Plan);
    private sealed record SessionMarker(string Format, string SessionId, string Token, string SnapshotSha256, string Fingerprint);
    private sealed record SnapshotEnvelope(string SessionId, string Token, InventorySnapshot Snapshot);
}
