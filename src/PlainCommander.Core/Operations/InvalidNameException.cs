namespace PlainCommander.Core.Operations;

public enum NameProblem
{
    Empty,
    BadCharacters,

    /// <summary>A name (one level of a path) longer than <see cref="SimpleCommands.MaxNameLength"/>.</summary>
    TooLong,
}

/// <summary>A typed name that cannot be a file or folder name; <see cref="ArgumentException"/> so existing handlers still catch it.</summary>
public sealed class InvalidNameException : ArgumentException
{
    public InvalidNameException(NameProblem problem, string name)
        : base(problem switch
        {
            NameProblem.Empty => "The name is empty.",
            NameProblem.TooLong => $"'{name}' is longer than {SimpleCommands.MaxNameLength} characters.",
            _ => $"'{name}' contains characters that are not allowed in a name.",
        }, nameof(name))
    {
        Problem = problem;
        Name = name;
    }

    public InvalidNameException()
    {
        Name = string.Empty;
    }

    public InvalidNameException(string message)
        : base(message)
    {
        Name = string.Empty;
    }

    public InvalidNameException(string message, Exception innerException)
        : base(message, innerException)
    {
        Name = string.Empty;
    }

    public NameProblem Problem { get; }

    public string Name { get; }
}
