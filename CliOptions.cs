using System.Globalization;

internal sealed record CliOptions(string Command, string[] Values, string Mode = "extractive", int TimeoutSeconds = 45)
{
    public const string Usage = "Usage: add <path> <category> <id> | ask [--mode extractive|generative] [--timeout 1..180] [--] <question> | delete <id>";

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException(Usage);
        string command = args[0].ToLowerInvariant();
        if (command == "add" && args.Length == 4) return new(command, args[1..]);
        if (command == "delete" && args.Length == 2) return new(command, args[1..]);
        if (command != "ask") throw new ArgumentException(Usage);
        string mode = "extractive";
        int timeout = 45, index = 1;
        while (index < args.Length && args[index].StartsWith("--", StringComparison.Ordinal))
        {
            string option = args[index++];
            if (option == "--") break;
            if (index >= args.Length) throw new ArgumentException(Usage);
            string value = args[index++];
            if (option == "--mode" && (value == "extractive" || value == "generative")) mode = value;
            else if (option == "--timeout" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) && seconds is >= 1 and <= 180) timeout = seconds;
            else throw new ArgumentException(Usage);
        }
        string question = string.Join(' ', args.Skip(index));
        if (question.Length is < 2 or > 500) throw new ArgumentException("Question must contain 2..500 characters. " + Usage);
        return new(command, [question], mode, timeout);
    }
}
