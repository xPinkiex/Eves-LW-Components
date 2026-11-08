using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LogicAPI.Server.Components;
using LogicLog;

namespace EVE.Components.Server
{
    /// Eve's Network Port: a plain byte pipe between the circuit and one TCP
    /// connection, listening or connecting out (docs/network-port.html).
    ///
    /// Inputs:  0-7 data, 8-15 control (8 SEND, 9 CMD, 10 HANGUP), 16 WR (rising
    ///          edge samples 0-15), 17 RD_DATA (high then low: the byte shown was
    ///          read, show the next), 18 RD_STATUS (high then low: HUP and ECODE
    ///          were seen, clear them).
    /// Outputs: 0-7 the received byte, 8 READY, 9 CONN, 10 LSN, 11 ROOM, 12 HUP
    ///          (the other side closed the connection, or it broke, since the last
    ///          status read; not our own HANGUP or close), 13-15 ECODE
    ///          (the last error since the last status read).
    /// Commands, bytes written with CMD and ended by '\n' or '\r':
    ///   listen PORT, listen ADDR:PORT, connect HOST:PORT, close.
    /// A worker thread does the network I/O; the logic update only moves bytes
    /// between the pins and the queues.
    public class NetworkPortLogic : LogicComponent
    {
        public const int WR = 16, RD_DATA = 17, RD_STATUS = 18;
        public const int SEND = 1 << 8, CMD = 1 << 9, HANGUP = 1 << 10;
        public const int READY = 8, CONN = 9, LSN = 10, ROOM = 11, HUP = 12, ECODE = 13;
        public const int QueueLimit = 4096;

        public const int EBadCommand = 1, EPortInUse = 2, EHostNotFound = 3, ERefused = 4,
                         ETimeout = 5, EOther = 6;

        private static readonly ILogicLogger Log = LogicLogger.For<NetworkPortLogic>();

        private readonly ConcurrentQueue<byte> _rx = new ConcurrentQueue<byte>();
        // What the circuit writes, in order: a byte to send, a command line, or
        // a hang-up (so nothing overtakes the bytes written before it).
        private enum Kind { Byte, Command, Hangup }
        private readonly ConcurrentQueue<(Kind Kind, string Command, byte Byte)> _out =
            new ConcurrentQueue<(Kind, string, byte)>();
        private readonly StringBuilder _commandLine = new StringBuilder();
        private int _txCount, _rxCount;
        private volatile bool _connected, _listening, _disposed;
        private int _hup, _ecode;               // sticky, until RD_STATUS
        // HANGUP drops CONN at once: written, not yet done on the worker thread.
        private int _hangupsAsked, _hangupsDone;
        private Thread _worker;

        private int _shown = -1;                // the byte on the outputs, -1 none
        private bool _prevWr, _prevRdData, _prevRdStatus;

        // Worker thread state
        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;

        protected override void Initialize()
        {
            _worker = new Thread(WorkerRun) { IsBackground = true, Name = "EVE NetworkPort" };
            _worker.Start();
        }

        public override bool InputAtIndexShouldTriggerComponentLogicUpdates(int inputIndex)
        {
            return inputIndex >= WR;
        }

        protected override void DoLogicUpdate()
        {
            bool wr = Inputs[WR].On, rdData = Inputs[RD_DATA].On, rdStatus = Inputs[RD_STATUS].On;
            if (wr && !_prevWr)
            {
                int word = 0;
                for (int i = 0; i < 16; i++)
                    if (Inputs[i].On) word |= 1 << i;
                Write(word);
            }
            if (!rdData && _prevRdData)
                _shown = -1;                    // read: show the next one
            if (!rdStatus && _prevRdStatus)
            {
                Interlocked.Exchange(ref _hup, 0);  // seen
                Interlocked.Exchange(ref _ecode, 0);
            }
            _prevWr = wr;
            _prevRdData = rdData;
            _prevRdStatus = rdStatus;

            if (_shown < 0 && _rx.TryDequeue(out byte next))
            {
                Interlocked.Decrement(ref _rxCount);
                _shown = next;
            }
            int status = (_shown >= 0 ? _shown : 0)
                         | (_shown >= 0 ? 1 << READY : 0)
                         | (_connected && Volatile.Read(ref _hangupsAsked) == Volatile.Read(ref _hangupsDone)
                            ? 1 << CONN : 0)
                         | (_listening ? 1 << LSN : 0)
                         | (Volatile.Read(ref _txCount) < QueueLimit ? 1 << ROOM : 0)
                         | (Volatile.Read(ref _hup) != 0 ? 1 << HUP : 0)
                         | (Volatile.Read(ref _ecode) & 7) << ECODE;
            for (int i = 0; i < 16; i++)
                Outputs[i].On = (status & (1 << i)) != 0;

            // Bytes and connections arrive on the worker thread: look again next tick.
            QueueLogicUpdate();
        }

        private void Write(int word)
        {
            byte b = (byte)word;
            if ((word & CMD) != 0)
            {
                if (b == '\n' || b == '\r')
                {
                    if (_commandLine.Length > 0)
                        _out.Enqueue((Kind.Command, _commandLine.ToString(), 0));
                    _commandLine.Clear();
                }
                else if (_commandLine.Length < 256)
                {
                    _commandLine.Append((char)b);
                }
            }
            else if ((word & SEND) != 0 && Volatile.Read(ref _txCount) < QueueLimit)
            {
                _out.Enqueue((Kind.Byte, null, b));
                Interlocked.Increment(ref _txCount);
            }
            if ((word & HANGUP) != 0)
            {
                Interlocked.Increment(ref _hangupsAsked);   // CONN is low from now on
                _out.Enqueue((Kind.Hangup, null, 0));
            }
        }

        public override void Dispose()
        {
            _disposed = true;
            base.Dispose();
        }

        // ------------------------------------------------------------- worker

        private void WorkerRun()
        {
            var buf = new byte[1024];
            var outBuf = new List<byte>(1024);
            while (!_disposed)
            {
                try
                {
                    // The circuit's bytes, commands and hang-ups, in order.
                    while (_out.TryDequeue(out var item))
                    {
                        if (item.Kind == Kind.Byte)
                        {
                            Interlocked.Decrement(ref _txCount);
                            if (_client != null)
                                outBuf.Add(item.Byte);  // with nothing to send to, it's dropped
                            continue;
                        }
                        Send(outBuf);
                        if (item.Kind == Kind.Command)
                        {
                            RunCommand(item.Command);
                        }
                        else
                        {
                            CloseClient(false);         // hang up: listening goes on
                            _connected = false;
                            Interlocked.Increment(ref _hangupsDone);
                        }
                    }
                    Send(outBuf);

                    if (_listener != null && _client == null && _listener.Pending())
                    {
                        _client = _listener.AcceptTcpClient();
                        Opened();
                        Log.Info($"NetworkPort: connection from {_client.Client.RemoteEndPoint}");
                    }

                    if (_client != null)
                    {
                        int room = QueueLimit - Volatile.Read(ref _rxCount);
                        if (room > 0 && _client.Available > 0)
                        {
                            int n = _stream.Read(buf, 0, Math.Min(room, buf.Length));
                            for (int i = 0; i < n; i++)
                            {
                                _rx.Enqueue(buf[i]);
                                Interlocked.Increment(ref _rxCount);
                            }
                        }
                        else if (_client.Client.Poll(0, SelectMode.SelectRead) && _client.Available == 0)
                        {
                            CloseClient(true);  // the other end closed; its bytes stay to be read
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"NetworkPort: {ex.Message}");
                    Error(EOther);
                    CloseClient(true);  // the connection broke
                }
                _connected = _client != null;
                _listening = _listener != null;
                Thread.Sleep(2);
            }
            CloseClient(false);
            CloseListener();
        }

        private void Error(int code)
        {
            Interlocked.Exchange(ref _ecode, code);
        }

        private void Send(List<byte> bytes)
        {
            if (bytes.Count > 0 && _stream != null)
                _stream.Write(bytes.ToArray(), 0, bytes.Count);
            bytes.Clear();
        }

        private static int Code(SocketException ex)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.AddressAlreadyInUse: return EPortInUse;
                case SocketError.HostNotFound:
                case SocketError.NoData:
                case SocketError.TryAgain: return EHostNotFound;
                case SocketError.ConnectionRefused: return ERefused;
                case SocketError.TimedOut: return ETimeout;
                default: return EOther;
            }
        }

        private void RunCommand(string line)
        {
            string[] w = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (w.Length == 0) return;
            string cmd = w[0].ToLowerInvariant();
            try
            {
                if (cmd == "close")
                {
                    CloseClient(false);
                    CloseListener();
                }
                else if (cmd == "listen" && w.Length == 2)
                {
                    CloseClient(false);
                    CloseListener();
                    IPAddress addr = IPAddress.Loopback;
                    string port = w[1];
                    int colon = w[1].LastIndexOf(':');
                    if (colon >= 0)
                    {
                        addr = IPAddress.Parse(w[1].Substring(0, colon));
                        port = w[1].Substring(colon + 1);
                    }
                    _listener = new TcpListener(addr, int.Parse(port));
                    _listener.Start();
                    Log.Info($"NetworkPort: listening on {addr}:{port}");
                }
                else if (cmd == "connect" && w.Length == 2 && w[1].LastIndexOf(':') > 0)
                {
                    CloseClient(false);
                    CloseListener();
                    int colon = w[1].LastIndexOf(':');
                    var client = new TcpClient();
                    var task = client.ConnectAsync(w[1].Substring(0, colon), int.Parse(w[1].Substring(colon + 1)));
                    if (!task.Wait(TimeSpan.FromSeconds(10)))
                    {
                        client.Dispose();
                        Error(ETimeout);
                        Log.Info($"NetworkPort: connect {w[1]}: no answer in 10 s");
                        return;
                    }
                    _client = client;
                    Opened();
                    Log.Info($"NetworkPort: connected to {w[1]}");
                }
                else
                {
                    Error(EBadCommand);
                    Log.Info($"NetworkPort: bad command '{line}'");
                }
            }
            catch (AggregateException ex) when (ex.InnerException is SocketException se)
            {
                Error(Code(se));
                Log.Info($"NetworkPort: '{line}' failed: {se.Message}");
            }
            catch (SocketException se)
            {
                Error(Code(se));
                Log.Info($"NetworkPort: '{line}' failed: {se.Message}");
            }
            catch (FormatException)
            {
                Error(EBadCommand);
                Log.Info($"NetworkPort: bad command '{line}'");
            }
            catch (Exception ex)
            {
                Error(EOther);
                Log.Info($"NetworkPort: '{line}' failed: {ex.Message}");
            }
            _connected = _client != null;
            _listening = _listener != null;
        }

        private void Opened()
        {
            _client.NoDelay = true;
            _stream = _client.GetStream();
        }

        // remote: the other side closed it, or it broke (HUP); else our own doing.
        private void CloseClient(bool remote)
        {
            if (_client != null && remote)
                Interlocked.Exchange(ref _hup, 1);
            try { _client?.Close(); } catch (Exception) { }
            _client = null;
            _stream = null;
        }

        private void CloseListener()
        {
            try { _listener?.Stop(); } catch (Exception) { }
            _listener = null;
        }
    }
}
