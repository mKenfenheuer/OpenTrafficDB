namespace OpenTrafficDB.Shared.Models.DTOs;

public record MatchJobRequest(string[] TripIds);
public record MatchJobResult(int Matched);
public record AggregateJobRequest(string[] TripIds);
public record AggregateJobResult(int Processed);
