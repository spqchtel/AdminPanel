using System.Text;
using MQTTnet;
using MQTTnet.Client;

public class Program
{
    
    private static readonly string MqttHost  = Env("MQTT_HOST", "localhost");
    private static readonly int    MqttPort  = int.Parse(Env("MQTT_PORT", "1883"));
    private static readonly string TopicRead = Env("MQTT_TOPIC_READ", "door/card/read");   // ESP32 publishes the UID here
    private static readonly string TopicCmd  = Env("MQTT_TOPIC_CMD", "door/lock/cmd");     // panel answers OPEN / DENY here

    private static readonly string DataDir  = Env("DATA_DIR", Path.Combine(AppContext.BaseDirectory, "Data"));
    private static readonly string CardFile = Path.Combine(DataDir, "card.csv");  // uid;name;status
    private static readonly string LogFile  = Path.Combine(DataDir, "access.log.csv"); // timestamp;uid;name;result

    private static readonly object FileLock = new();
    private static IMqttClient? mqtt;
    private static bool exiting;

    private static readonly Dictionary<string, Action<string[]>> commandList =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["add"]     = AddCard,
            ["list"]    = ListCards,
            ["remove"]  = RemoveCard,
            ["block"]   = a => SetStatus(a, "blocked"),
            ["unblock"] = a => SetStatus(a, "active"),
            ["log"]     = ShowLog,
            ["help"]    = _ => PrintHelp(),
        };

    public static async Task Main()
    {
        Directory.CreateDirectory(DataDir);
        if (!File.Exists(CardFile)) File.WriteAllText(CardFile, "");
        if (!File.Exists(LogFile)) File.WriteAllText(LogFile, "");

        Console.WriteLine("Door access admin panel");
        await StartMqttAsync();
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

            if (command.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

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

        exiting = true;
        if (mqtt != null && mqtt.IsConnected)
            await mqtt.DisconnectAsync();
    }

   

    private static async Task StartMqttAsync()
    {
        var factory = new MqttFactory();
        mqtt = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(MqttHost, MqttPort)
            .WithClientId("admin-panel-" + Environment.MachineName)
            .WithCleanSession()
            .Build();

        mqtt.ApplicationMessageReceivedAsync += async e =>
        {
            string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            await HandleCardReadAsync(payload);
        };

        // simple auto-reconnect
        mqtt.DisconnectedAsync += async _ =>
        {
            if (exiting) return;
            Console.WriteLine("\n[MQTT] disconnected, retrying in 5s...");
            while (!exiting)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    await mqtt.ConnectAsync(options);
                    await mqtt.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(TopicRead).Build());
                    Console.WriteLine("[MQTT] reconnected");
                    return;
                }
                catch { /* try again */ }
            }
        };

        try
        {
            await mqtt.ConnectAsync(options);
            await mqtt.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(TopicRead).Build());
            Console.WriteLine($"[MQTT] connected to {MqttHost}:{MqttPort}, listening on '{TopicRead}'");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MQTT] could not connect: {ex.Message} (admin commands still work)");
        }
    }

    private static async Task HandleCardReadAsync(string payload)
    {
        string uid = NormalizeUid(payload);
        string result;
        string name = "-";

        lock (FileLock)
        {
            var card = ReadCards().FirstOrDefault(c => c.Uid == uid);
            if (card == null)
            {
                result = "UNKNOWN";
            }
            else
            {
                name = card.Name;
                result = card.Status.Equals("active", StringComparison.OrdinalIgnoreCase) ? "GRANTED" : "DENIED";
            }

            File.AppendAllText(LogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss};{uid};{name};{result}{Environment.NewLine}");
        }

        Console.WriteLine($"\n[READ] {DateTime.Now:HH:mm:ss} UID={uid} Name={name} -> {result}");
        Console.Write("> ");

       
        if (mqtt != null && mqtt.IsConnected)
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(TopicCmd)
                .WithPayload(result == "GRANTED" ? "OPEN" : "DENY")
                .Build();
            await mqtt.PublishAsync(msg);
        }
    }

    

    private static void PrintHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  add <UID> <name>   add a card");
        Console.WriteLine("  list               show all cards");
        Console.WriteLine("  remove <UID>       delete a card");
        Console.WriteLine("  block <UID>        block a card");
        Console.WriteLine("  unblock <UID>      re-activate a card");
        Console.WriteLine("  log [n]            show the last n card reads (default 20)");
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

        string uid = NormalizeUid(args[0]);
        string name = string.Join(" ", args.Skip(1)).Replace(';', ',');

        lock (FileLock)
        {
            if (ReadCards().Any(c => c.Uid == uid))
            {
                Console.WriteLine("A card with this UID already exists.");
                return;
            }
            File.AppendAllText(CardFile, $"{uid};{name};active{Environment.NewLine}");
        }

        Console.WriteLine($"Card added: UID={uid}, name={name}");
    }

    private static void ListCards(string[] args)
    {
        List<Card> cards;
        lock (FileLock) cards = ReadCards();

        if (cards.Count == 0)
        {
            Console.WriteLine("No cards stored.");
            return;
        }

        foreach (var c in cards)
            Console.WriteLine($"UID: {c.Uid} | Name: {c.Name} | Status: {c.Status}");
    }

    private static void RemoveCard(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("Usage: remove <UID>"); return; }
        string uid = NormalizeUid(args[0]);

        lock (FileLock)
        {
            var cards = ReadCards();
            int removed = cards.RemoveAll(c => c.Uid == uid);
            if (removed == 0) { Console.WriteLine("UID not found."); return; }
            WriteCards(cards);
        }
        Console.WriteLine("Card removed.");
    }

    private static void SetStatus(string[] args, string status)
    {
        if (args.Length < 1) { Console.WriteLine($"Usage: {(status == "blocked" ? "block" : "unblock")} <UID>"); return; }
        string uid = NormalizeUid(args[0]);

        lock (FileLock)
        {
            var cards = ReadCards();
            var card = cards.FirstOrDefault(c => c.Uid == uid);
            if (card == null) { Console.WriteLine("UID not found."); return; }
            card.Status = status;
            WriteCards(cards);
        }
        Console.WriteLine($"Card {uid} is now {status}.");
    }

    private static void ShowLog(string[] args)
    {
        int count = 20;
        if (args.Length > 0 && !int.TryParse(args[0], out count)) count = 20;

        string[] lines;
        lock (FileLock) lines = File.ReadAllLines(LogFile);

        foreach (string line in lines.TakeLast(count))
        {
            string[] f = line.Split(';');
            if (f.Length < 4) continue;
            Console.WriteLine($"{f[0]} | UID: {f[1]} | Name: {f[2]} | {f[3]}");
        }
    }

    // ------------------------------------------------------------- Helpers

    private class Card
    {
        public string Uid = "";
        public string Name = "";
        public string Status = "active";
    }

    private static List<Card> ReadCards()
    {
        var list = new List<Card>();
        if (!File.Exists(CardFile)) return list;

        foreach (string line in File.ReadAllLines(CardFile))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = line.Split(';');
            if (f.Length < 2) continue;
            list.Add(new Card
            {
                Uid = f[0].Trim().ToUpper(),
                Name = f[1].Trim(),
                Status = f.Length > 2 ? f[2].Trim() : "active"
            });
        }
        return list;
    }

    private static void WriteCards(List<Card> cards)
    {
        File.WriteAllLines(CardFile, cards.Select(c => $"{c.Uid};{c.Name};{c.Status}"));
    }

    // "04:a3:2f:1b", "04 A3 2F 1B" and "04A32F1B" all become "04A32F1B"
    private static string NormalizeUid(string raw) =>
        raw.Trim().Replace(":", "").Replace("-", "").Replace(" ", "").ToUpper();

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) ?? fallback;
}
