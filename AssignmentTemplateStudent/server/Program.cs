using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            serverSocket.ReceiveTimeout = 5000;

            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"Server started on {serverEndPoint}");
            Console.ResetColor();

            while (true)
            {
                Console.WriteLine("\nWaiting for new messages...");
                bool clientSessionActive = true;
                int expectedLookups = 4;
                int receivedLookups = 0;
                
                while (clientSessionActive)
                {
                    try
                    {
                        byte[] buffer = new byte[1024];
                        EndPoint clientEndPoint = new IPEndPoint(IPAddress.Any, 0);
                        int bytesReceived = serverSocket.ReceiveFrom(buffer, ref clientEndPoint);
                        string receivedMessage = Encoding.ASCII.GetString(buffer, 0, bytesReceived);
                        
                        ProcessMessage(serverSocket, receivedMessage, (IPEndPoint)clientEndPoint, ref receivedLookups, ref clientSessionActive, expectedLookups);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        continue;
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
                                     ref int receivedLookups, ref bool clientSessionActive, int expectedLookups)
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
                    receivedLookups++;
                    try
                    {
                        var lookupRecord = JsonSerializer.Deserialize<DNSRecord>(
                            JsonSerializer.Serialize(clientMessage.Content));
                        
                        var foundRecord = dnsRecords.FirstOrDefault(
                            r => r.Name == lookupRecord.Name && r.Type == lookupRecord.Type);

                        if (foundRecord != null)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("Found record");
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("No matching record found");
                        }
                        Console.ResetColor();

                        Message responseMessage = foundRecord != null
                            ? new Message
                            {
                                MsgId = clientMessage.MsgId,
                                MsgType = MessageType.DNSLookupReply,
                                Content = foundRecord
                            }
                            : new Message
                            {
                                MsgId = clientMessage.MsgId,
                                MsgType = MessageType.Error,
                                Content = "Domain not found"
                            };

                        string responseType = foundRecord != null ? "DNSLookupReply message" : "Error message";
                        SendMessage(socket, responseMessage, clientEndPoint, responseType);
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Error processing DNS lookup: {ex.Message}");
                        Console.ResetColor();
                        SendMessage(socket, new Message
                        {
                            MsgId = clientMessage.MsgId,
                            MsgType = MessageType.Error,
                            Content = "Invalid DNS lookup request"
                        }, clientEndPoint, "Error message");
                    }

                    if (receivedLookups >= expectedLookups)
                    {
                        SendMessage(socket, new Message
                        {
                            MsgId = new Random().Next(1000, 9999),
                            MsgType = MessageType.End,
                            Content = "End of DNSLookup session"
                        }, clientEndPoint, "End message");
                        clientSessionActive = false;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("\nSent End message to client");
                        Console.ResetColor();
                    }
                    break;

                case MessageType.Ack:
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Received Ack for MsgId: {clientMessage.Content}");
                    Console.ResetColor();
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
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nError sending message: {ex.Message}");
            Console.ResetColor();
        }
    }
}
//Hallo meneer Omar Ahmad :p