﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;
using NLog;


using ZitiDesktopEdge.DataStructures;

namespace ZitiDesktopEdge.ServiceClient {
    public abstract class AbstractClient {

        private bool _extendedDebug = false; //set ZITI_EXTENDED_DEBUG env var to true if you want to diagnose issues with the service comms

        protected NamedPipeClientStream pipeClient = null;
        protected NamedPipeClientStream eventClient = null;
        protected StreamWriter ipcWriter = null;
        protected StreamReader ipcReader = null;
        protected abstract Task ConnectPipesAsync();
        protected abstract void ProcessLine(string line);
        protected abstract Logger Logger { get; }

        protected const string localPipeServer = ".";
        protected const int ServiceConnectTimeout = 500;

        //protected object namedPipeSyncLock = new object();
        protected static SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

        protected JsonSerializer serializer = new JsonSerializer() { Formatting = Formatting.None };

        protected virtual void ClientConnected(object e) {
            Connected = true;
            Reconnecting = false;
            CleanShutdown = false;
            Logger.Debug("Client connected successfully. Setting CleanShutdown set to false.");

            ipcWriter = new StreamWriter(pipeClient);
            ipcReader = new StreamReader(pipeClient);
            Task.Run(async () => { //hack for now until it's async...
                try {
                    StreamReader eventReader = new StreamReader(eventClient);
                    while (true) {
                        if (eventReader.EndOfStream) {
                            break;
                        }

                        try {
                            string respAsString = await readMessageAsync(eventReader, "ClientConnected");
                            ProcessLine(respAsString);
                        } catch (Exception ex) {
                            Logger.Warn(ex, "ERROR caught");
                        }
                    }
                } catch (Exception ex) {
                    Logger.Debug("unepxected error: " + ex.ToString());
                }

                // since this thread is always sitting waiting to read
                // it should be the only one triggering this event
                ClientDisconnected(null);
            });
            OnClientConnected?.Invoke(this, e);
        }

        protected virtual void ClientDisconnected(object e) {
            Reconnect();
            Connected = false;
            OnClientDisconnected?.Invoke(this, e);
        }

        protected virtual void ShutdownEvent(StatusEvent e) {
            Logger.Debug("Clean shutdown detected from ziti");
            CleanShutdown = true;
            OnShutdownEvent?.Invoke(this, e);
        }

        async protected Task sendAsync(object objToSend) {
            bool retried = false;
            while (true) {
                try {
                    string toSend = JsonConvert.SerializeObject(objToSend, Formatting.None);

                    if (toSend?.Trim() != null) {
                        debugServiceCommunication("===============  sending message =============== ");
                        debugServiceCommunication(toSend);
                        await ipcWriter.WriteAsync(toSend);
                        await ipcWriter.WriteAsync('\n');
                        debugServiceCommunication("=============== flushing message =============== ");
                        await ipcWriter.FlushAsync();
                        debugServiceCommunication("===============     sent message =============== ");
                        debugServiceCommunication("");
                        debugServiceCommunication("");
                    } else {
                        Logger.Debug("NOT sending empty object??? " + objToSend?.ToString());
                    }
                    break;
                } catch (IOException ioe) {
                    //almost certainly a problem with the pipe - recreate the pipe... try one more time.
                    //setupPipe();
                    if (retried) {
                        //we tried - throw the error...
                        throw ioe;
                    } else {
                        retried = true; //fall back through to the while and try again
                    }
                } catch (Exception ex) {
                    //if this fails it's usually because the writer is null/invalid. throwing IOException
                    //will trigger the pipe to rebuild
                    throw new IOException("Unexpected error when sending data to service. " + ex.Message);
                }
            }
        }

        public event EventHandler<object> OnClientConnected = null;
        public event EventHandler<object> OnClientDisconnected;
        public event EventHandler<StatusEvent> OnShutdownEvent;

        public bool Reconnecting { get; set; }
        public bool Connected { get; set; }
        public bool CleanShutdown { get; set; }

        public AbstractClient() {
            try {
                string extDebugEnv = Environment.GetEnvironmentVariable("ZITI_EXTENDED_DEBUG");
                if (extDebugEnv != null) {
                    if (bool.Parse(extDebugEnv)) {
                        _extendedDebug = true;
                    }
                }
            } catch (Exception ex) {
                Logger.Debug("EXCEPTION IN CLIENT CONNECT: " + ex.Message);
                //if this happens - enter retry mode...
                Reconnect();
            }
        }

        async public Task ConnectAsync() {
            //establish the named pipe to the service
            await ConnectPipesAsync();
        }

        public void Reconnect() {
            if (Reconnecting) {
                Logger.Debug("Already in reconnect mode.");
                return;
            } else {
                Reconnecting = true;
            }

            Task.Run(async () => {
                Logger.Info("service is down. attempting to connect to service...");

                DateTime reconnectStart = DateTime.Now;
                DateTime logAgainAfter = reconnectStart + TimeSpan.FromSeconds(1);

                while (true) {
                    try {
                        await Task.Delay(2500);
                        await ConnectPipesAsync();

                        if (Connected) {
                            Logger.Debug("Connected to the service - exiting reconect loop");
                            Connected = true;
                            Reconnecting = false;
                            return;
                        } else {
                            //ClientDisconnected(null);
                        }
                    } catch {
                        var now = DateTime.Now;
                        if (now > logAgainAfter) {
                            Logger.Debug("Reconnect failed. Trying again...");
                            var duration = now - reconnectStart;
                            if (duration > TimeSpan.FromHours(1)) {
                                Logger.Info("reconnect has not completed and has been running for {0} hours", duration.TotalHours);
                                logAgainAfter += TimeSpan.FromHours(1);
                            } else if (duration > TimeSpan.FromMinutes(1)) {
                                Logger.Info("reconnect has not completed and has been running for {0} minutes", duration.TotalMinutes);
                                logAgainAfter += TimeSpan.FromMinutes(1);
                            } else {
                                logAgainAfter += TimeSpan.FromSeconds(1);
                            }
                        }
                    }
                }
            });
        }

        protected void debugServiceCommunication(string msg) {
            if (_extendedDebug) {
                Logger.Debug(msg);
            }
        }

        async protected Task<T> read<T>(StreamReader reader, string where) where T : SvcResponse {
            string respAsString = await readMessageAsync(reader, where);
            T resp = (T)serializer.Deserialize(new StringReader(respAsString), typeof(T));
            return resp;
        }

        async public Task<string> readMessageAsync(StreamReader reader, string where) {
            try {/*
                if (reader.EndOfStream) {
                    throw new ServiceException("the pipe has closed", 0, "end of stream reached");
                }*/
                int emptyCount = 1; //just a stop gap in case something crazy happens in the communication

                debugServiceCommunication("==============a  reading message =============== " + where);
                string respAsString = await reader.ReadLineAsync();
                debugServiceCommunication(respAsString);
                debugServiceCommunication("===============     read message =============== " + where);
                while (string.IsNullOrEmpty(respAsString?.Trim())) {
                    /*if (reader.EndOfStream) {
                        throw new Exception("the pipe has closed");
                    }*/
                    debugServiceCommunication("Received empty payload - continuing to read until a payload is received");
                    //now how'd that happen...
                    debugServiceCommunication("==============b  reading message =============== " + where);
                    respAsString = await reader.ReadLineAsync();
                    debugServiceCommunication(respAsString);
                    debugServiceCommunication("===============     read message =============== " + where);
                    emptyCount++;
                    if (emptyCount > 5) {
                        Logger.Debug("are we there yet? " + reader.EndOfStream);
                        //that's just too many...
                        //setupPipe();
                        return null;
                    }
                }
                debugServiceCommunication("");
                debugServiceCommunication("");
                return respAsString;
            } catch (IOException ioe) {
                //almost certainly a problem with the pipe
                Logger.Debug("io error in read: " + ioe.Message);
                ClientDisconnected(null);
                throw ioe;
            } catch (Exception ee) {
                //almost certainly a problem with the pipe
                Logger.Debug("unexpected error in read: " + ee.Message);
                ClientDisconnected(null);
                throw ee;
            }
        }

        async public Task WaitForConnectionAsync() {
            while (Reconnecting || !Connected) {
                await Task.Delay(100);
            }
        }
    }
}
