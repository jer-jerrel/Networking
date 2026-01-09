using System.Collections.Immutable;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LibData;

class Program
{
    static void Main(string[] args)
    {
        ClientUDP.Start();
    }
}

public class Setting
{
    public int ServerPortNumber { get; set; }
    public string? ServerIPAddress { get; set; }
    public int ClientPortNumber { get; set; }
    public string? ClientIPAddress { get; set; }
}

class ClientUDP
{
    static string configFile = @"../Setting.json";
    static string dnsRecordsFile = @"../server/DNSrecords.json";
    static Setting? setting;
    static List<DNSRecord> dnsRecords;

    public static void Start()
    {
        try
        {
            string configContent = File.ReadAllText(configFile);
            setting = JsonSerializer.Deserialize<Setting>(configContent);

            Socket clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IPEndPoint serverEndPoint = new IPEndPoint(IPAddress.Parse(setting.ServerIPAddress), setting.ServerPortNumber);
            IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Parse(setting.ClientIPAddress), setting.ClientPortNumber);
            clientSocket.Bind(localEndPoint);
            clientSocket.ReceiveTimeout = 10000;

            string clientAddress = $"{setting.ClientIPAddress}:{setting.ClientPortNumber}";
            string serverAddress = $"{setting.ServerIPAddress}:{setting.ServerPortNumber}";

            // Send HELLO and receive WELCOME
            Message helloMessage = new Message 
            { 
                MsgId = 1, 
                MsgType = MessageType.Hello, 
                Content = "Hello from client" 
            };
            SendMessage(clientSocket, helloMessage, serverEndPoint, clientAddress, serverAddress, "Hello message");

            Message welcomeMessage = ReceiveMessage(clientSocket);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Server → Client({clientAddress}): Welcome message");
            Console.WriteLine($" {JsonSerializer.Serialize(welcomeMessage)}\n");
            Console.ResetColor();

            //DNS lookups
            string recordsContent = File.ReadAllText(dnsRecordsFile);
            dnsRecords = JsonSerializer.Deserialize<List<DNSRecord>>(recordsContent);

            List<Message> lookupMessages = new List<Message>
            {
                new Message { 
                    MsgId = 33, 
                    MsgType = MessageType.DNSLookup, 
                    Content = new DNSRecord { Type = "A", Name = "www.outlook.com" } 
                },
                new Message { 
                    MsgId = 44, 
                    MsgType = MessageType.DNSLookup, 
                    Content = new DNSRecord { Type = "MX", Name = "example.com" } 
                },
                new Message { 
                    MsgId = 55, 
                    MsgType = MessageType.DNSLookup, 
                    Content = new DNSRecord { Type = "A", Name = "poep" } 
                },
                new Message { 
                    MsgId = 66, 
                    MsgType = MessageType.DNSLookup, 
                    Content = new DNSRecord { Type = "CNAME", Name = "invalid.domain" } 
                }
            };

            foreach (var lookupMessage in lookupMessages)
            {
                try
                {
                    SendMessage(clientSocket, lookupMessage, serverEndPoint, clientAddress, serverAddress, "DNSLookup message");
                    Message replyMessage = ReceiveMessage(clientSocket);
                    
                    string messageTypeDescription = replyMessage.MsgType switch
                    {
                        MessageType.DNSLookupReply => "DNSLookupReply message",
                        MessageType.Error => "Error message",
                        _ => $"{replyMessage.MsgType} message"
                    };
                    
                    if (replyMessage.MsgType == MessageType.DNSLookupReply)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                    }
                    else if (replyMessage.MsgType == MessageType.Error)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Blue;
                    }
                    
                    Console.WriteLine($"Server → Client({clientAddress}): {messageTypeDescription}");
                    Console.WriteLine($" {JsonSerializer.Serialize(replyMessage)}\n");
                    Console.ResetColor();

                    if (replyMessage.MsgType == MessageType.DNSLookupReply)
                    {
                        Message ackMessage = new Message 
                        { 
                            MsgId = new Random().Next(1000, 9999), 
                            MsgType = MessageType.Ack, 
                            Content = lookupMessage.MsgId.ToString() 
                        };
                        SendMessage(clientSocket, ackMessage, serverEndPoint, clientAddress, serverAddress, "Ack message");
                        Console.WriteLine();
                    }
                }
                catch (TimeoutException ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error: {ex.Message}. Skipping this lookup.");
                    Console.ResetColor();
                }
            }

            // Receive End message
            Message endMessage = ReceiveMessage(clientSocket);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Server → Client({clientAddress}): End message");
            Console.WriteLine($" {JsonSerializer.Serialize(endMessage)}");
            Console.WriteLine($"\nReceived End Message: {endMessage.Content}");
            Console.ResetColor();

            clientSocket.Close();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Client error: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static Message ReceiveMessage(Socket socket)
    {
        byte[] buffer = new byte[1024];
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        
        int maxRetries = 3;
        int attempt = 0;
        
        while (attempt < maxRetries)
        {
            try
            {
                int bytesReceived = socket.ReceiveFrom(buffer, ref remoteEP);
                string receivedMessage = Encoding.ASCII.GetString(buffer, 0, bytesReceived);

                var options = new JsonSerializerOptions
                {
                    Converters = { new JsonStringEnumConverter() }
                };
                return JsonSerializer.Deserialize<Message>(receivedMessage, options);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                attempt++;
                if (attempt >= maxRetries)
                {
                    throw new TimeoutException("No response received from server... Trying again...");
                }
            }
        }
        throw new TimeoutException("No response received from server after retries.");
    }

    private static void SendMessage(Socket socket, Message message, IPEndPoint endPoint, 
                                  string clientAddress, string serverAddress, string messageType)
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        };
        string jsonMessage = JsonSerializer.Serialize(message, options);
        byte[] buffer = Encoding.ASCII.GetBytes(jsonMessage);
        socket.SendTo(buffer, endPoint);
        
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"Client({clientAddress}) → Server: {messageType}");
        Console.WriteLine($" {jsonMessage}");
        Console.ResetColor();
    }
}