/*  
    This file is part of IFS.

    IFS is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    IFS is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with IFS.  If not, see <http://www.gnu.org/licenses/>.
*/

using IFS.BSP;
using IFS.Logging;

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace IFS.Exp
{

    public class ExpWorker : BSPWorkerBase
    {
        public ExpWorker(BSPChannel channel) : base(channel)
        {
            // Register for channel events
            channel.OnDestroy += OnChannelDestroyed;

            _running = true;

            _workerThread = new Thread(new ThreadStart(ExpWorkerThreadInit));
            _workerThread.Start();            
        }

        public override void Terminate()
        {
            Logging.Log.Write(LogType.Error, LogComponent.Exp, "Terminate");

            ShutdownWorker();
        }

        private void OnChannelDestroyed(BSPChannel channel)
        {
            Logging.Log.Write(LogType.Error, LogComponent.Exp, "OnChannelDestroyed");

            ShutdownWorker();
        }

        private void ExpWorkerThreadInit()
        {            
            //
            // Run the worker thread.
            // If anything goes wrong, log the exception and tear down the BSP connection.
            //
            try
            {
                ExpWorkerThread();
            }
            catch(Exception e)
            {
                if (!(e is ThreadAbortException))
                {
                    Logging.Log.Write(LogType.Error, LogComponent.Exp, "Exp worker thread terminated with exception '{0}'.", e.Message);
                    Channel.SendAbort("Server encountered an error.");

                    OnExit(this);
                }
            }
        }

        private void ExpWorkerThread()
        {
            // TODO: enforce state (i.e. reject out-of-order block types.)
            byte[] data = new byte[1];
            string result = "";
            while (_running)
            {
                int length = Channel.Read(ref data, 1);
                if (length < 1)
                {
                    int mark = Channel.LastMark;
                    // See FtpTelnet.bcpl
                    if (mark == 1)
                    {
                        Log.Write(LogComponent.Exp, "Got Sync mark {0}", mark);
                    } else if (mark == 2)
                    {
                        int lineWidth = Channel.ReadByte();
                        Log.Write(LogComponent.Exp, "Got LineWidth {0}", lineWidth);
                        DoSocket();
                    } else if (mark == 3)
                    {
                        int pageLength = Channel.ReadByte();
                        Log.Write(LogComponent.Exp, "Got page length mark {0}", pageLength);
                    }
                    else if (mark == 4)
                    {
                        int terminalType = Channel.ReadByte();
                        Log.Write(LogComponent.Exp, "Got terminal type mark {0}", terminalType);
                    }
                    else if (mark == 5)
                    {
                        Log.Write(LogComponent.Exp, "Got timing mark {0}", mark);
                        Channel.SendMark(6, true /* ack */); // Timing reply mark
                    }
                    else if (mark == 6)
                    {
                        Log.Write(LogComponent.Exp, "Got timing reply mark {0}", mark);
                    }
                    else
                    {
                        Log.Write(LogComponent.Exp, "Unexpected mark {0}", mark);

                    }
                }
                else
                {
                    Log.Write(LogComponent.Exp, "Got char {0}", data[0]);
                    result += Convert.ToChar(data[0]);
                    if (data[0] == 13)
                    {
                        Log.Write(LogComponent.Exp, "Got line {0}", result);
                        string reply = "You sent: " + result;
                        Channel.Send(Encoding.ASCII.GetBytes(reply));
                        result = "";
                    }
                }

            }
         
        }        

        private void DoSocket()
        {
            IPHostEntry hostEntry = Dns.GetHostEntry("google.com");
            IPAddress address = hostEntry.AddressList[0];

            string request = "GET /\r\n";
            Byte[] bytesSent = Encoding.ASCII.GetBytes(request);
            Byte[] bytesReceived = new Byte[1000];
            IPEndPoint ipe = new IPEndPoint(address, 80);
            Socket s = new Socket(ipe.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            s.Connect(ipe);
            if (s == null)
            {
                Log.Write(LogComponent.Exp, "connect failed");
                Channel.Destroy();
                return;
            }
            s.Send(bytesSent, bytesSent.Length, 0);
            int bytes = 0;
                bytes = s.Receive(bytesReceived, bytesReceived.Length, 0);
                string data = Encoding.ASCII.GetString(bytesReceived, 0, bytes);
            Log.Write(LogType.Verbose, LogComponent.Exp, "Received {0}", data);
            Channel.Send(Encoding.ASCII.GetBytes(data));
        }

        private void ShutdownWorker()
        {
            // Tell the thread to exit and give it a short period to do so...
            _running = false;

            Log.Write(LogType.Verbose, LogComponent.Exp, "Asking Exp worker thread to exit...");
            _workerThread.Join(1000);

            if (_workerThread.IsAlive)
            {
                Logging.Log.Write(LogType.Verbose, LogComponent.Exp, "Exp worker thread did not exit, terminating.");
                _workerThread.Abort();

                if (OnExit != null)
                {
                    OnExit(this);
                }
            }            
        }

       


        private Thread _workerThread;
        private bool _running;

    }
}
