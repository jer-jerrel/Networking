using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Timers;
using Timer = System.Timers.Timer;
using LibData;

class Program
{
    static void Main(string[] args)
    {
        ServerUDP.Start();
    }
}

public class Setting
{
    public int ServerPortNumber { get; set; }
    public string? ServerIPAddress { get; set; }
    public int ClientPortNumber { get; set; }
    public string? ClientIPAddress { get; set; }
}

class ServerUDP
{
    static string configFile = @"../Setting.json";
    static string dnsRecordsFile = @"DNSrecords.json";
    static Setting? setting;
    static List<DNSRecord> dnsRecords;

    private static Dictionary<int, (Message message, int retries, Timer retryTimer)> retryBuffer = new();

    private static List<DNSRecord> LoadDNSRecords()
    {
        try
        {
            string recordsContent = File.ReadAllText(dnsRecordsFile);
            return JsonSerializer.Deserialize<List<DNSRecord>>(recordsContent) ?? new List<DNSRecord>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading DNS records: {ex.Message}");
            return new List<DNSRecord>();
        }
    }

    public static void Start()
    {
        try
        {
            string configContent = File.ReadAllText(configFile);
            setting = JsonSerializer.Deserialize<Setting>(configContent);
            dnsRecords = LoadDNSRecords();

            Socket serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IPEndPoint serverEndPoint = new IPEndPoint(IPAddress.Parse(setting.ServerIPAddress), setting.ServerPortNumber);
            serverSocket.Bind(serverEndPoint);
            serverSocket.ReceiveTimeout = 8000;

            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"Server started on {serverEndPoint}");
            Console.ResetColor();

            bool announcedWaiting = false;

            while (true)
            {
                if (!announcedWaiting)
                {
                    Console.WriteLine("\nWaiting for new messages...");
                    announcedWaiting = true;
                }
                bool clientSessionActive = true;
                int expectedLookups = 4;

                int totalDNSLookups = 0;
                int dnsRepliesSent = 0;
                int dnsReplyAcksReceived = 0;

                IPEndPoint? lastClientEndPoint = null;

                while (clientSessionActive)
                {
                    try
                    {
                        byte[] buffer = new byte[1024];
                        EndPoint clientEndPoint = new IPEndPoint(IPAddress.Any, 0);
                        int bytesReceived = serverSocket.ReceiveFrom(buffer, ref clientEndPoint);
                        announcedWaiting = false;
                        string receivedMessage = Encoding.ASCII.GetString(buffer, 0, bytesReceived);

                        lastClientEndPoint = (IPEndPoint)clientEndPoint;

                        ProcessMessage(serverSocket, receivedMessage, (IPEndPoint)clientEndPoint,
                            ref totalDNSLookups, ref dnsRepliesSent, ref dnsReplyAcksReceived,
                            ref clientSessionActive, expectedLookups);
                        
                        if (totalDNSLookups >= expectedLookups && dnsReplyAcksReceived == dnsRepliesSent)
                        {
                            if (lastClientEndPoint != null)
                            {
                                SendMessage(serverSocket, new Message
                                {
                                    MsgId = new Random().Next(1000, 9999),
                                    MsgType = MessageType.End,
                                    Content = "End of DNSLookup"
                                }, lastClientEndPoint, "End message");
                            }
                            clientSessionActive = false;
                        }

                        if (!clientSessionActive)
                        {
                            foreach (var entry in retryBuffer.Values)
                            {
                                entry.retryTimer.Stop();
                                entry.retryTimer.Dispose();
                            }
                            retryBuffer.Clear();

                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"\nSession Summary:");
                            Console.WriteLine($"Total DNSLookups received: {totalDNSLookups}");
                            Console.WriteLine($"DNSReplies sent: {dnsRepliesSent}");
                            Console.WriteLine($"DNSReply ACKs received: {dnsReplyAcksReceived}");
                            Console.ResetColor();
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        if (lastClientEndPoint != null)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine("\nNo message received in 10 seconds. Sending End message to client.");
                            Console.ResetColor();

                            SendMessage(serverSocket, new Message
                            {
                                MsgId = new Random().Next(1000, 9999),
                                MsgType = MessageType.End,
                                Content = "End of DNSLookup"
                            }, lastClientEndPoint, "End message");
                        }

                        clientSessionActive = false;
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Error processing message: {ex.Message}");
                        Console.ResetColor();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nServer error: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void ProcessMessage(Socket socket, string receivedMessage, IPEndPoint clientEndPoint,
                                 ref int totalDNSLookups, ref int dnsRepliesSent, ref int dnsReplyAcksReceived,
                                 ref bool clientSessionActive, int expectedLookups)
    {
        try
        {
            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            Message clientMessage = JsonSerializer.Deserialize<Message>(receivedMessage, options);

            string receivedMessageType = clientMessage.MsgType switch
            {
                MessageType.Hello => "Hello message",
                MessageType.DNSLookup => "DNSLookup message",
                MessageType.Ack => "Ack message",
                _ => $"{clientMessage.MsgType} message"
            };

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"\nClient({clientEndPoint}) → Server: {receivedMessageType}");
            Console.WriteLine($" {receivedMessage}");
            Console.ResetColor();

            switch (clientMessage.MsgType)
            {
                case MessageType.Hello:
                    SendMessage(socket, new Message
                    {
                        MsgId = 4,
                        MsgType = MessageType.Welcome,
                        Content = "Welcome from server"
                    }, clientEndPoint, "Welcome message");
                    break;

                case MessageType.DNSLookup:
                    totalDNSLookups++;

                    try
                    {
                        var lookupRecord = JsonSerializer.Deserialize<DNSRecord>(
                            JsonSerializer.Serialize(clientMessage.Content));

                        if (!IsValidDomainName(lookupRecord.Name))
                        {
                            SendMessage(socket, new Message
                            {
                                MsgId = clientMessage.MsgId,
                                MsgType = MessageType.Error,
                                Content = "Domain name invalid"
                            }, clientEndPoint, "Error message");
                            break;
                        }

                        var foundRecord = dnsRecords.FirstOrDefault(
                            r => r.Name == lookupRecord.Name && r.Type == lookupRecord.Type);

                        if (foundRecord != null)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("Found record");
                            Console.ResetColor();

                            dnsRepliesSent++;

                            Message responseMessage = new Message
                            {
                                MsgId = clientMessage.MsgId,
                                MsgType = MessageType.DNSLookupReply,
                                Content = foundRecord
                            };

                            SendMessageWithRetry(socket, responseMessage, clientEndPoint, "DNSLookupReply message");
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("No matching record found");
                            Console.ResetColor();

                            SendMessage(socket, new Message
                            {
                                MsgId = clientMessage.MsgId,
                                MsgType = MessageType.Error,
                                Content = "Domain not found"
                            }, clientEndPoint, "Error message");
                        }
                    }
                    catch
                    {
                        SendMessage(socket, new Message
                        {
                            MsgId = clientMessage.MsgId,
                            MsgType = MessageType.Error,
                            Content = "Invalid DNS lookup request"
                        }, clientEndPoint, "Error message");
                    }
                    break;

                case MessageType.Ack:
                    try
                    {
                        int ackedMsgId = ParseAckContent(clientMessage.Content);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"Received Ack for MsgId: {ackedMsgId}");
                        Console.ResetColor();

                        if (retryBuffer.TryGetValue(ackedMsgId, out var entry) && entry.message.MsgType == MessageType.DNSLookupReply)
                        {
                            dnsReplyAcksReceived++;
                        }

                        RemoveFromRetryBuffer(ackedMsgId);
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Error processing Ack: {ex.Message}");
                        Console.ResetColor();
                    }
                    break;

                default:
                    Console.WriteLine($"Unexpected message type: {clientMessage.MsgType}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error deserializing message: {ex.Message}");
            Console.ResetColor();
            SendMessage(socket, new Message
            {
                MsgId = new Random().Next(1000, 9999),
                MsgType = MessageType.Error,
                Content = "Invalid message format"
            }, clientEndPoint, "Error message");
        }
    }

    private static void SendMessage(Socket socket, Message message, IPEndPoint endPoint, string messageType)
    {
        try
        {
            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            string jsonMessage = JsonSerializer.Serialize(message, options);
            byte[] buffer = Encoding.ASCII.GetBytes(jsonMessage);
            socket.SendTo(buffer, endPoint);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"Server → Client({endPoint}): {messageType}");
            Console.WriteLine($" {jsonMessage}");
            Console.ResetColor();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Client unreachable for MsgId {message.MsgId}, stopping retries.");
            Console.ResetColor();
            if (retryBuffer.TryGetValue(message.MsgId, out var entry))
            {
                entry.retryTimer.Stop();
                entry.retryTimer.Dispose();
                retryBuffer.Remove(message.MsgId);
            }
        }
    }

    private static void SendMessageWithRetry(Socket socket, Message message, IPEndPoint endPoint, string messageType)
    {
        SendMessage(socket, message, endPoint, messageType);
        Timer retryTimer = new Timer(3000);
        retryTimer.Elapsed += (s, e) => RetrySendMessage(s, e, socket, message, endPoint, messageType);
        retryTimer.AutoReset = false;
        retryTimer.Start();
        retryBuffer[message.MsgId] = (message, 0, retryTimer);
    }

    private static void RetrySendMessage(object? sender, ElapsedEventArgs e, Socket socket, Message message, IPEndPoint endPoint, string messageType)
    {
        if (retryBuffer.TryGetValue(message.MsgId, out var entry))
        {
            if (entry.retries < 3)
            {
                SendMessage(socket, message, endPoint, $"Retry {entry.retries + 1} for {messageType}");
                entry.retryTimer.Interval = 3000;
                entry.retryTimer.Start();
                retryBuffer[message.MsgId] = (message, entry.retries + 1, entry.retryTimer);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Giving up after 3 retries for MsgId: {message.MsgId}");
                Console.ResetColor();
                entry.retryTimer.Stop();
                entry.retryTimer.Dispose();
                retryBuffer.Remove(message.MsgId);
            }
        }
    }

    private static void RemoveFromRetryBuffer(int msgId)
    {
        if (retryBuffer.TryGetValue(msgId, out var entry))
        {
            entry.retryTimer.Stop();
            entry.retryTimer.Dispose();
            retryBuffer.Remove(msgId);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Removed MsgId {msgId} from retry buffer");
            Console.ResetColor();
        }
    }

    private static bool IsValidDomainName(string domainName)
    {
        if (string.IsNullOrWhiteSpace(domainName))
            return false;

        const string pattern = @"^(?=.{1,253}$)(?!-)[a-zA-Z0-9-]{1,63}(?<!-)(\.(?!-)[a-zA-Z0-9-]{1,63}(?<!-))*\.?$";
        return Regex.IsMatch(domainName, pattern);
    }

    private static int ParseAckContent(object? content)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        if (content is JsonElement je && je.ValueKind == JsonValueKind.Number)
        {
            return je.GetInt32();
        }
        if (int.TryParse(content.ToString(), out int id))
        {
            return id;
        }
        throw new FormatException("ACK content is not a valid integer MsgId");
    }
}
