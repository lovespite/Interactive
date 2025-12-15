namespace Interactive.Utilities;

public class ConsoleHelper
{
    public static ConsoleHelper Instance { get; } = new ConsoleHelper();
    public string? InputPromptPrefix { get; set; } = "> ";

    private readonly Lock @lock = new();

    public void Print(object? message)
    {
        lock (@lock) Console.Write(message);
    }

    public void PrintLine(object? message)
    {
        lock (@lock) Console.WriteLine(message);
    }

    public void WriteLine(object? message)
    {
        lock (@lock)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }
    }

    public void NextLine() => PrintLine(string.Empty);

    public void Write(object? message) => Print(message);

    public void PrintError(object? message)
    {
        lock (@lock)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }
    }

    public void PrintWarning(object? message)
    {
        lock (@lock)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }
    }

    public void PrintInfo(object? message)
    {
        lock (@lock)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }
    }

    public void PrintSuccess(object? message)
    {
        lock (@lock)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }
    }

    public void PrintLow(object? message)
    {
        lock (@lock)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }
    }

    public void Clear() => Console.Clear();

    public string GetInputLine(string? prompt = null)
    {
        prompt ??= InputPromptPrefix;
        if (prompt is not null)
        {
            lock (@lock)
            {
                var originalColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(prompt);
                Console.ForegroundColor = originalColor;
            }
        }

        try
        {
            return Console.ReadLine() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public ConsoleKeyInfo GetInputKey(string? prompt = null)
    {
        if (prompt is not null) PrintLow(prompt);
        return Console.ReadKey(true);
    }
}
