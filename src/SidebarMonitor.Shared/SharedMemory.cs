namespace SidebarMonitor.Shared;

/// <summary>The main agent -> UI channel. Thin names over the generic seqlock.</summary>
public static class SnapshotChannel
{
    public static SeqLockWriter<Snapshot> CreateWriter() =>
        new(SnapshotLayout.MapName, SnapshotLayout.Signature, SnapshotLayout.Version);

    public static SeqLockReader<Snapshot>? TryOpenReader(out string? error) =>
        SeqLockReader<Snapshot>.TryOpen(SnapshotLayout.MapName, SnapshotLayout.Signature, SnapshotLayout.Version, out error);
}
