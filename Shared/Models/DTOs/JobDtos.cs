namespace OpenTrafficDB.Shared.Models.DTOs;

public record MatchJobRequest(string[] TripIds);
public record MatchJobResult(int Matched);
public record AggregateJobRequest(long[] WayIds);
public record AggregateJobResult(int Processed);
