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
    /// Eve's Serial Port: a byte stream between the circuit and a TCP connection,
    /// like a UART whose other end is a socket. A worker thread does the network
    /// I/O; the logic update only moves bytes between the pins and two queues.
    ///
    /// Inputs:  0-7 TX data (bit 0 = input 0), 8 WR (rising edge: the TX byte is
    ///          sent, or with CMD high, added to the command line), 9 CMD, 10 RD
    ///          (falling edge: the shown RX byte is consumed, the next one shown).
    /// Outputs: 0-7 RX data, 8 RXV (a byte is shown), 9 CONN (connected),
    ///          10 LSN (listening), 11 TXR (room to send), 12 ERR (the last
    ///          command failed; cleared by the next one).
    ///
    /// Commands (bytes written with CMD high, ended by '\n' or '\r'):
    ///   listen PORT [telnet]       accept one connection at a time on 127.0.0.1
    ///   listen ADDR:PORT [telnet]  on that address (0.0.0.0: every interface)
    ///   connect HOST:PORT          connect out
    ///   close                      close everything
    /// With `telnet` the block asks the client for character mode (it echoes
    /// nothing itself: the program does), drops telnet commands from the input
    /// and turns CR LF / CR NUL into CR, so `telnet localhost PORT` is a terminal.
    public class SerialPortLogic : LogicComponent
    {
        public const int TxBase = 0, WR = 8, CMD = 9, RD = 10;
        public const int RxBase = 0, RXV = 8, CONN = 9, LSN = 10, TXR = 11, ERR = 12;
        public const int QueueLimit = 4096;

        private static readonly ILogicLogger Log = LogicLogger.For<SerialPortLogic>();

        private readonly ConcurrentQueue<byte> _rx = new ConcurrentQueue<byte>();
        // What the program writes, in order: a byte to send, or a command line
        // (so a command never overtakes the bytes written before it).
        private readonly ConcurrentQueue<(string Command, byte Byte)> _out =
            new ConcurrentQueue<(string, byte)>();
        private readonly StringBuilder _commandLine = new StringBuilder();
        private int _txCount, _rxCount;
        private volatile bool _connected, _listening, _error, _disposed;
        private Thread _worker;

        private int _shown = -1;            // the RX byte on the outputs, -1 none
        private bool _prevWr, _prevRd;

        // Worker thread state
        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private bool _telnet;
        private int _telnetState;           // 0 data, 1 after IAC, 2 option byte, 3 sub, 4 sub IAC, 5 after CR

        protected override void Initialize()
        {
            _worker = new Thread(WorkerRun) { IsBackground = true, Name = "EVE SerialPort" };
            _worker.Start();
        }

        public override bool InputAtIndexShouldTriggerComponentLogicUpdates(int inputIndex)
        {
            return inputIndex == WR || inputIndex == RD;
        }

        protected override void DoLogicUpdate()
        {
            bool wr = Inputs[WR].On, rd = Inputs[RD].On;
            if (wr && !_prevWr)
            {
                byte b = 0;
                for (int i = 0; i < 8; i++)
                    if (Inputs[TxBase + i].On) b |= (byte)(1 << i);
                if (Inputs[CMD].On)
                    AddCommandByte(b);
                else if (Volatile.Read(ref _txCount) < QueueLimit)
                {
                    _out.Enqueue((null, b));
                    Interlocked.Increment(ref _txCount);
                }
            }
            if (!rd && _prevRd)
                _shown = -1;                // consumed: show the next one
            _prevWr = wr;
            _prevRd = rd;

            if (_shown < 0 && _rx.TryDequeue(out byte next))
            {
                Interlocked.Decrement(ref _rxCount);
                _shown = next;
            }
            for (int i = 0; i < 8; i++)
                Outputs[RxBase + i].On = _shown >= 0 && (_shown & (1 << i)) != 0;
            Outputs[RXV].On = _shown >= 0;
            Outputs[CONN].On = _connected;
            Outputs[LSN].On = _listening;
            Outputs[TXR].On = Volatile.Read(ref _txCount) < QueueLimit;
            Outputs[ERR].On = _error;

            // Bytes and connections arrive on the worker thread: look again next tick.
            QueueLogicUpdate();
        }

        private void AddCommandByte(byte b)
        {
            if (b == '\n' || b == '\r')
            {
                if (_commandLine.Length > 0)
                    _out.Enqueue((_commandLine.ToString(), 0));
                _commandLine.Clear();
            }
            else if (_commandLine.Length < 256)
            {
                _commandLine.Append((char)b);
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
                    // The program's bytes and commands, in order.
                    outBuf.Clear();
                    while (_out.TryDequeue(out var item))
                    {
                        if (item.Command != null)
                        {
                            Send(outBuf);
                            RunCommand(item.Command);
                            continue;
                        }
                        Interlocked.Decrement(ref _txCount);
                        if (_client != null)
                            outBuf.Add(item.Byte);   // with nothing to send to, it's dropped
                    }
                    Send(outBuf);

                    if (_listener != null && _client == null && _listener.Pending())
                    {
                        _client = _listener.AcceptTcpClient();
                        Opened();
                        Log.Info($"SerialPort: connection from {_client.Client.RemoteEndPoint}");
                    }

                    if (_client != null)
                    {
                        int room = QueueLimit - Volatile.Read(ref _rxCount);
                        if (room > 0 && _client.Available > 0)
                        {
                            int n = _stream.Read(buf, 0, Math.Min(room, buf.Length));
                            for (int i = 0; i < n; i++)
                                Receive(buf[i]);
                        }
                        else if (_client.Client.Poll(0, SelectMode.SelectRead) && _client.Available == 0)
                        {
                            Log.Info("SerialPort: the other end closed the connection");
                            CloseClient();      // listening goes on; RX bytes stay to be read
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"SerialPort: {ex.Message}");
                    _error = true;
                    CloseClient();
                }
                _connected = _client != null;
                _listening = _listener != null;
                Thread.Sleep(2);
            }
            CloseClient();
            CloseListener();
        }

        private void Send(List<byte> bytes)
        {
            if (bytes.Count > 0 && _stream != null)
                _stream.Write(bytes.ToArray(), 0, bytes.Count);
            bytes.Clear();
        }

        private void RunCommand(string line)
        {
            _error = false;
            string[] w = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (w.Length == 0) return;
            try
            {
                switch (w[0].ToLowerInvariant())
                {
                    case "listen":
                    {
                        CloseClient();
                        CloseListener();
                        if (w.Length < 2) throw new ArgumentException("listen PORT");
                        IPAddress addr = IPAddress.Loopback;
                        string port = w[1];
                        int colon = w[1].LastIndexOf(':');
                        if (colon >= 0)
                        {
                            addr = IPAddress.Parse(w[1].Substring(0, colon));
                            port = w[1].Substring(colon + 1);
                        }
                        _telnet = w.Length > 2 && w[2].ToLowerInvariant() == "telnet";
                        _listener = new TcpListener(addr, int.Parse(port));
                        _listener.Start();
                        Log.Info($"SerialPort: listening on {addr}:{port}{(_telnet ? " (telnet)" : "")}");
                        break;
                    }
                    case "connect":
                    {
                        CloseClient();
                        CloseListener();
                        if (w.Length < 2) throw new ArgumentException("connect HOST:PORT");
                        int colon = w[1].LastIndexOf(':');
                        if (colon < 0) throw new ArgumentException("connect HOST:PORT");
                        _telnet = false;
                        var client = new TcpClient();
                        if (!client.ConnectAsync(w[1].Substring(0, colon), int.Parse(w[1].Substring(colon + 1)))
                                   .Wait(TimeSpan.FromSeconds(10)))
                            throw new TimeoutException($"connect {w[1]}: no answer in 10 s");
                        _client = client;
                        Opened();
                        Log.Info($"SerialPort: connected to {w[1]}");
                        break;
                    }
                    case "close":
                        CloseClient();
                        CloseListener();
                        break;
                    default:
                        throw new ArgumentException($"unknown command '{w[0]}'");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"SerialPort: '{line}' failed: {ex.Message}");
                _error = true;
            }
            _connected = _client != null;
            _listening = _listener != null;
        }

        private void Opened()
        {
            _client.NoDelay = true;
            _stream = _client.GetStream();
            _telnetState = 0;
            if (_telnet)    // IAC WILL ECHO, IAC WILL SUPPRESS-GO-AHEAD: character mode, no local echo
                _stream.Write(new byte[] { 255, 251, 1, 255, 251, 3 }, 0, 6);
        }

        private void Receive(byte b)
        {
            if (_telnet)
            {
                switch (_telnetState)
                {
                    case 1:                         // after IAC
                        if (b == 255) { _telnetState = 0; break; }  // IAC IAC: a 255 byte
                        _telnetState = b == 250 ? 3 : b >= 251 ? 2 : 0;
                        return;
                    case 2: _telnetState = 0; return;               // the option of WILL/WONT/DO/DONT
                    case 3: if (b == 255) _telnetState = 4; return; // inside SB ... IAC SE
                    case 4: _telnetState = b == 240 ? 0 : 3; return;
                    case 5:                         // after CR: drop LF or NUL
                        _telnetState = 0;
                        if (b == 10 || b == 0) return;
                        break;
                    default:
                        if (b == 255) { _telnetState = 1; return; }
                        break;
                }
                if (b == 13) _telnetState = 5;
            }
            _rx.Enqueue(b);
            Interlocked.Increment(ref _rxCount);
        }

        private void CloseClient()
        {
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
