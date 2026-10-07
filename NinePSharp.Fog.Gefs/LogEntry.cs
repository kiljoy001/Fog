namespace NinePSharp.Fog.Gefs;

// An allocation log entry; a sync barrier's offset is the generation it commits.
internal readonly record struct LogEntry(LogOp Op, long Offset, long Length);
