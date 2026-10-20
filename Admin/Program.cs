using System.Xml;

public class Program
{
    private static readonly Dictionary<string, Action<string[]>> commandList =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["add"] = AddCard,
            ["list"] = ListCards,
            ["help"] = _ => PrintHelp(),
        };

    public static void Main()
    {
        Console.WriteLine("Door access admin panel");
        PrintHelp();

        while (true)
        {
            Console.Write("> ");
            string? line = Console.ReadLine();

            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string command = parts[0];
            string[] commandArgs = parts.Skip(1).ToArray();

            if (command.ToLower().Equals("exit")) break;

            if (!commandList.TryGetValue(command, out var action))
            {
                Console.WriteLine("Unknown command");
                continue;
            }

            try
            {
                action(commandArgs);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  add <UID> <name>   add a card");
        Console.WriteLine("  list               show all cards");
        Console.WriteLine("  help               show this text");
        Console.WriteLine("  exit               quit");
    }

    private static void AddCard(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: add <UID> <name>");
            return;
        }

        string uid = args[0].ToUpper();
        string name = string.Join(" ", args.Skip(1));

        Console.WriteLine($"add called: UID={uid}, name={name}");
    }

    private static void ListCards(string[] args)
    {
        string[] lines = File.ReadAllLines(card);
        foreach(string line in lines) {
            string[] fields = line.Split(';');
            string uid = fields[0];
            string name = fields[1];
            string status = fields[2];
        }
        Console.WriteLine("list called");
        Console.WriteLine(uid);
        Console.WriteLine(name);
        Console.WriteLine(status);

    }
}