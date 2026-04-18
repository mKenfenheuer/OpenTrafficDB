namespace OpenTrafficDBModels.DTOs;

public record MatchJobResult(int Matched, long[] MatchedWayIds, string[] FailedTripIds);
public record AggregateJobResult(int Processed);
