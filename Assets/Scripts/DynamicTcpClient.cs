using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// ATOMIC DEPLOY CHECK: lobby TCP client bundle 2026-06-29 protocol v2.
public class DynamicTcpClient : MonoBehaviour
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static DynamicTcpClient activeClient;

    [Header("Default Server")]
    [SerializeField] private string host = "play.atomichost.xyz";
    [SerializeField] private int port = 25661;

    [Header("Connection")]
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private string helloMessage = "Hello from Unity";

    private TcpClient client;
    private NetworkStream stream;
    private CancellationTokenSource cancellation;
    private Task connectTask;
    private readonly ConcurrentQueue<string> receivedMessages = new();
    private readonly ConcurrentDictionary<int, string> latestStateMessages = new();
    private readonly StringBuilder receiveBuffer = new();

    public bool IsConnected => client != null && client.Connected;
    public string Host => host;
    public int Port => port;
    public int LocalPlayerId { get; private set; } = -1;
    public static DynamicTcpClient ActiveClient => activeClient;
    public event Action<string> MessageReceived;

    public string[] GetCachedStateMessages()
    {
        string[] snapshot = new string[latestStateMessages.Count];
        latestStateMessages.Values.CopyTo(snapshot, 0);
        return snapshot;
    }

    private void Awake()
    {
        if (activeClient != null && activeClient != this)
        {
            Destroy(gameObject);
            return;
        }

        activeClient = this;
        transform.SetParent(null);
        DontDestroyOnLoad(gameObject);
    }

    private async void Start()
    {
        ApplyCommandLineOverrides();

        if (connectOnStart)
        {
            await ConnectAsync();
        }
    }

    public async Task ConnectAsync()
    {
        await ConnectAsync(host, port);
    }

    public async Task ConnectAsync(string serverAddress)
    {
        if (!TrySplitAddress(serverAddress, out string parsedHost, out int parsedPort))
        {
            Debug.LogError($"Invalid TCP server address: {serverAddress}");
            return;
        }

        await ConnectAsync(parsedHost, parsedPort);
    }

    public async Task ConnectAsync(string serverHost, int serverPort)
    {
        if (IsConnected && host == serverHost && port == serverPort)
        {
            return;
        }

        if (connectTask != null && !connectTask.IsCompleted)
        {
            await connectTask;

            if (IsConnected && host == serverHost && port == serverPort)
            {
                return;
            }
        }

        connectTask = ConnectInternalAsync(serverHost, serverPort);

        try
        {
            await connectTask;
        }
        finally
        {
            if (connectTask != null && connectTask.IsCompleted)
            {
                connectTask = null;
            }
        }
    }

    private async Task ConnectInternalAsync(string serverHost, int serverPort)
    {
        Disconnect();

        host = serverHost;
        port = serverPort;
        cancellation = new CancellationTokenSource();

        try
        {
            client = new TcpClient();
            await client.ConnectAsync(host, port);
            stream = client.GetStream();

            Debug.Log($"Connected to TCP server {host}:{port}");
            _ = ReceiveLoopAsync(cancellation.Token);

            if (!string.IsNullOrWhiteSpace(helloMessage))
            {
                await WriteMessageAsync(helloMessage);
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to connect to TCP server {host}:{port}: {exception.Message}");
            Disconnect();
        }
    }

    private void Update()
    {
        while (receivedMessages.TryDequeue(out string message))
        {
            CacheConnectionState(message);
            DispatchMessage(message);
        }
    }

    // Invoke each subscriber independently so that one throwing handler (for example a
    // stale menu controller that lingers after the scene change) cannot abort the multicast
    // and starve later subscribers such as NetworkPlayerSync.
    private void DispatchMessage(string message)
    {
        Action<string> handlers = MessageReceived;

        if (handlers == null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)handler).Invoke(message);
            }
            catch (Exception exception)
            {
                Debug.LogError($"TCP message handler error: {exception.Message}");
            }
        }
    }

    public void SetServer(string serverHost, int serverPort)
    {
        host = serverHost;
        port = serverPort;
    }

    public void SetPort(int serverPort)
    {
        port = serverPort;
    }

    public async Task SendAsync(string message)
    {
        if ((stream == null || !IsConnected) && connectTask != null && !connectTask.IsCompleted)
        {
            await connectTask;
        }

        if (stream == null || !IsConnected)
        {
            Debug.LogWarning("TCP send skipped because the client is not connected.");
            return;
        }

        await WriteMessageAsync(message);
    }

    private async Task WriteMessageAsync(string message)
    {
        byte[] data = Utf8NoBom.GetBytes(message + "\n");
        Task writeTask = stream.WriteAsync(data, 0, data.Length);
        Task completedTask = await Task.WhenAny(writeTask, Task.Delay(2000));

        if (completedTask != writeTask)
        {
            Debug.LogWarning($"TCP send timed out: {message.Split('|')[0]}");
            return;
        }

        await writeTask;
    }

    public async Task CreateServerListingAsync(int maxPlayers)
    {
        await CreateServerListingAsync(maxPlayers, string.Empty);
    }

    public async Task CreateServerListingAsync(int maxPlayers, string lobbyName)
    {
        int clampedMaxPlayers = Mathf.Clamp(maxPlayers, 1, 12);
        string sanitizedLobbyName = SanitizeMessagePart(lobbyName);

        if (!IsConnected)
        {
            await ConnectAsync();
        }

        await SendAsync($"create_listing|{clampedMaxPlayers}|{sanitizedLobbyName}");
    }

    public async Task JoinServerListingAsync(int listingId)
    {
        if (!IsConnected)
        {
            await ConnectAsync();
        }

        await SendAsync($"join_listing|{Mathf.Max(0, listingId)}");
    }

    private static string SanitizeMessagePart(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace("|", " ").Replace("\r", " ").Replace("\n", " ");
    }

    public void Disconnect()
    {
        LocalPlayerId = -1;
        latestStateMessages.Clear();

        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;

        stream?.Close();
        stream = null;

        client?.Close();
        client = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        byte[] buffer = new byte[4096];

        while (!token.IsCancellationRequested && stream != null && IsConnected)
        {
            try
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    Debug.Log("TCP server closed the connection.");
                    Disconnect();
                    return;
                }

                EnqueueReceivedText(Utf8NoBom.GetString(buffer, 0, bytesRead));
            }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested)
                {
                    Debug.LogError($"TCP receive error: {exception.Message}");
                    Disconnect();
                }

                return;
            }
        }
    }

    private void EnqueueReceivedText(string text)
    {
        receiveBuffer.Append(text);

        while (true)
        {
            string bufferedText = receiveBuffer.ToString();
            int newlineIndex = bufferedText.IndexOf('\n');

            if (newlineIndex < 0)
            {
                return;
            }

            string message = bufferedText[..newlineIndex].Trim();
            receiveBuffer.Remove(0, newlineIndex + 1);

            if (!string.IsNullOrWhiteSpace(message))
            {
                receivedMessages.Enqueue(message);
            }
        }
    }

    private void CacheConnectionState(string message)
    {
        string[] parts = message.Split('|');

        if (parts.Length == 2 && parts[0].Trim().Trim('\uFEFF') == "welcome" && int.TryParse(parts[1], out int playerId))
        {
            LocalPlayerId = playerId;
            return;
        }

        if (parts.Length == 9 && parts[0] == "state" && int.TryParse(parts[1], out int statePlayerId))
        {
            latestStateMessages[statePlayerId] = message;
            return;
        }

        if (parts.Length == 2 && parts[0] == "leave" && int.TryParse(parts[1], out int leavingPlayerId))
        {
            latestStateMessages.TryRemove(leavingPlayerId, out _);
        }
    }

    private void ApplyCommandLineOverrides()
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-server" && i + 1 < args.Length)
            {
                ConnectAddressFromString(args[i + 1]);
                i++;
            }
            else if (args[i] == "-serverHost" && i + 1 < args.Length)
            {
                host = args[i + 1];
                i++;
            }
            else if ((args[i] == "-serverPort" || args[i] == "-port") && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedPort))
            {
                port = parsedPort;
                i++;
            }
        }
    }

    private void ConnectAddressFromString(string serverAddress)
    {
        if (TrySplitAddress(serverAddress, out string parsedHost, out int parsedPort))
        {
            host = parsedHost;
            port = parsedPort;
        }
        else
        {
            Debug.LogWarning($"Ignoring invalid -server value: {serverAddress}");
        }
    }

    private static bool TrySplitAddress(string serverAddress, out string parsedHost, out int parsedPort)
    {
        parsedHost = string.Empty;
        parsedPort = 0;

        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            return false;
        }

        string[] parts = serverAddress.Trim().Split(':');

        if (parts.Length != 2 || !int.TryParse(parts[1], out parsedPort))
        {
            return false;
        }

        parsedHost = parts[0];
        return !string.IsNullOrWhiteSpace(parsedHost) && parsedPort > 0 && parsedPort <= 65535;
    }

    private void OnApplicationQuit()
    {
        Disconnect();
    }

    private void OnDestroy()
    {
        if (activeClient == this)
        {
            activeClient = null;
        }

        Disconnect();
    }
}
