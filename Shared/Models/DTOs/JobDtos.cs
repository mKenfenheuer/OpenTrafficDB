namespace OpenTrafficDB.Shared.Models.DTOs;

public record MatchJobRequest(DateTime From, DateTime To);
public record MatchJobResult(int Matched);
public record AggregateJobRequest(DateTime From, DateTime To);
public record AggregateJobResult(int Processed);
