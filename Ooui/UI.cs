using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.WebSockets;

namespace Ooui
{
    public static class UI
    {
        static CancellationTokenSource serverCts;

        static readonly Dictionary<string, Func<Element>> publishedPaths =
            new Dictionary<string, Func<Element>> ();

        static readonly byte[] clientJsBytes;

        public static string Template { get; set; } = $@"<!DOCTYPE html>
<html>
<head>
  <title>@ElementPath</title>
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
  <link rel=""stylesheet"" href=""https://maxcdn.bootstrapcdn.com/bootstrap/3.3.7/css/bootstrap.min.css"" integrity=""sha384-BVYiiSIFeK1dGmJRAkycuHAHRg32OmUcww7on3RYdg4Va+PmSTsz/K68vbdEjh4u"" crossorigin=""anonymous"">
</head>
<body>
<div id=""ooui-body"" class=""container-fluid""></div>
<script src=""/ooui.js""></script>
<script>ooui(""@ElementPath"");</script>
</body>
</html>";

        static string host = "*";
        public static string Host {
            get => host;
            set {
                if (!string.IsNullOrWhiteSpace (value) && host != value) {
                    host = value;
                    Restart ();
                }
            }
        }
        static int port = 8080;
        public static int Port {
            get => port;
            set {
                if (port != value) {
                    port = value;
                    Restart ();
                }
            }
        }

        static UI ()
        {
            var asm = typeof(UI).Assembly;
            // System.Console.WriteLine("ASM = {0}", asm);
            // foreach (var n in asm.GetManifestResourceNames()) {
            //     System.Console.WriteLine("  {0}", n);
            // }
            using (var s = asm.GetManifestResourceStream ("Ooui.Client.js")) {
                using (var r = new StreamReader (s)) {
                    clientJsBytes = Encoding.UTF8.GetBytes (r.ReadToEnd ());
                }
            }
        }

        public static void Publish (string path, Func<Element> elementCtor)
        {
            Console.WriteLine ($"PUBLISH {path}");
            lock (publishedPaths) publishedPaths[path] = elementCtor;
            Start ();
        }

        public static void Publish (string path, Element element)
        {
            Publish (path, () => element);
        }

        static void Start ()
        {
            if (serverCts != null) return;
            serverCts = new CancellationTokenSource ();
            var token = serverCts.Token;
            var listenerPrefix = $"http://{host}:{port}/";
            Task.Run (() => RunAsync (listenerPrefix, token), token);
        }

        static void Stop ()
        {
            var scts = serverCts;
            if (scts == null) return;
            serverCts = null;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine ($"Stopping...");
            Console.ResetColor ();

            scts.Cancel ();
        }

        static void Restart ()
        {
            if (serverCts == null) return;
            Stop ();
            Start ();
        }

        static async Task RunAsync (string listenerPrefix, CancellationToken token)
        {
            HttpListener listener = null;

            var started = false;
            while (!started && !token.IsCancellationRequested) {
                try {
                    listener = new HttpListener ();
                    listener.Prefixes.Add (listenerPrefix);
                    listener.Start ();
                    started = true;
                }
                catch (System.Net.Sockets.SocketException ex) when
                    (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse) {
                    var wait = 5;
                    Console.WriteLine ($"{listenerPrefix} is in use, trying again in {wait} seconds...");
                    await Task.Delay (wait * 1000).ConfigureAwait (false);
                }
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine ($"Listening at {listenerPrefix}...");
            Console.ResetColor ();

            while (!token.IsCancellationRequested) {
                var listenerContext = await listener.GetContextAsync ().ConfigureAwait (false);
                if (listenerContext.Request.IsWebSocketRequest) {
                    ProcessWebSocketRequest (listenerContext, token);
                }
                else {
                    ProcessRequest (listenerContext, token);
                }
            }
        }

        static void ProcessRequest (HttpListenerContext listenerContext, CancellationToken token)
        {
            var url = listenerContext.Request.Url;
            var path = url.LocalPath;

            Console.WriteLine ($"{listenerContext.Request.HttpMethod} {url.LocalPath}");

            var response = listenerContext.Response;

            Func<Element> ctor;

            if (path == "/ooui.js") {
                response.ContentLength64 = clientJsBytes.LongLength;
                response.ContentType = "application/javascript";
                response.ContentEncoding = Encoding.UTF8;
                response.AddHeader ("Cache-Control", "public, max-age=3600");
                using (var s = response.OutputStream) {
                    s.Write (clientJsBytes, 0, clientJsBytes.Length);
                }
            }
            else {
                var found = false;
                lock (publishedPaths) found = publishedPaths.TryGetValue (path, out ctor);
                if (found) {
                    WriteElementHtml (path, response);
                }
                else {
                    response.StatusCode = 404;
                    response.Close ();
                }
            }
        }

        static string RenderTemplate (string elementPath)
        {
            return Template.Replace ("@ElementPath", elementPath);
        }

        static void WriteElementHtml (string elementPath, HttpListenerResponse response)
        {
            response.StatusCode = 200;
            response.ContentType = "text/html";
            response.ContentEncoding = Encoding.UTF8;
            var html = Encoding.UTF8.GetBytes (RenderTemplate (elementPath));
            response.ContentLength64 = html.LongLength;
            using (var s = response.OutputStream) {
                s.Write (html, 0, html.Length);
            }
            response.Close ();
        }

        static async void ProcessWebSocketRequest (HttpListenerContext listenerContext, CancellationToken serverToken)
        {
            //
            // Find the element
            //
            var url = listenerContext.Request.Url;
            var path = url.LocalPath;

            Func<Element> ctor;
            var found = false;
            lock (publishedPaths) found = publishedPaths.TryGetValue (path, out ctor);
            if (!found) {
                listenerContext.Response.StatusCode = 404;
                listenerContext.Response.Close ();
                return;
            }

            Element element = null;
            try {
                element = ctor ();
            }
            catch (Exception ex) {
                listenerContext.Response.StatusCode = 500;
                listenerContext.Response.Close();
                Error ("Failed to create element", ex);
                return;
            }

            //
            // Connect the web socket
            //
            WebSocketContext webSocketContext = null;
            WebSocket webSocket = null;
            try {
                webSocketContext = await listenerContext.AcceptWebSocketAsync (subProtocol: "ooui").ConfigureAwait (false);
                webSocket = webSocketContext.WebSocket;
                Console.WriteLine ("WEBSOCKET {0}", listenerContext.Request.Url.LocalPath);
            }
            catch (Exception ex) {
                listenerContext.Response.StatusCode = 500;
                listenerContext.Response.Close();
                Error ("Failed to accept WebSocket", ex);
                return;
            }

            //
            // Create a new session and let it handle everything from here
            //
            try {
                var session = new Session (webSocket, element, serverToken);
                await session.RunAsync ().ConfigureAwait (false);
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely) {
                // The remote party closed the WebSocket connection without completing the close handshake.
            }
            catch (Exception ex) {
                Error ("Web socket failed", ex);
            }
            finally {
                webSocket?.Dispose ();
            }
        }

        static void Error (string message, Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine ("{0}: {1}", message, ex);
            Console.ResetColor ();
        }

        class Session
        {
            readonly WebSocket webSocket;
            readonly Element element;
            readonly Action<Message> handleElementMessageSent;

            readonly CancellationTokenSource sessionCts = new CancellationTokenSource ();
            readonly CancellationTokenSource linkedCts;
            readonly CancellationToken token;

            readonly HashSet<string> createdIds;
            readonly List<Message> queuedMessages = new List<Message> ();

            readonly System.Timers.Timer sendThrottle;
            DateTime lastTransmitTime = DateTime.MinValue;
            readonly TimeSpan throttleInterval = TimeSpan.FromSeconds (1.0 / 30); // 30 FPS max

            public Session (WebSocket webSocket, Element element, CancellationToken serverToken)
            {
                this.webSocket = webSocket;
                this.element = element;

                //
                // Create a new session cancellation token that will trigger
                // automatically if the server shutsdown or the session shutsdown.
                //
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource (serverToken, sessionCts.Token);
                token = linkedCts.Token;

                //
                // Keep a list of all the elements for which we've transmitted the initial state
                //
                createdIds = new HashSet<string> {
                    "window",
                    "document",
                    "document.body",
                };

                //
                // Preparse handlers for the element
                //
                handleElementMessageSent = QueueMessage;

                //
                // Create a timer to use as a throttle when sending messages
                //
                sendThrottle = new System.Timers.Timer (throttleInterval.TotalMilliseconds);
                sendThrottle.Elapsed += (s, e) => {
                    // System.Console.WriteLine ("TICK SEND THROTTLE FOR {0}", element);
                    if ((e.SignalTime - lastTransmitTime) >= throttleInterval) {
                        sendThrottle.Enabled = false;
                        lastTransmitTime = e.SignalTime;
                        TransmitQueuedMessages ();
                    }
                };
            }

            public async Task RunAsync ()
            {
                //
                // Start watching for changes in the element
                //
                element.MessageSent += handleElementMessageSent;

                try {
                    //
                    // Add it to the document body
                    //
                    QueueMessage (Message.Call ("document.body", "appendChild", element));

                    //
                    // Start the Read Loop
                    //
                    var receiveBuffer = new byte[64*1024];

                    while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested) {
                        var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token).ConfigureAwait (false);

                        if (receiveResult.MessageType == WebSocketMessageType.Close) {
                            await webSocket.CloseAsync (WebSocketCloseStatus.NormalClosure, "", token).ConfigureAwait (false);
                            sessionCts.Cancel ();
                        }
                        else if (receiveResult.MessageType == WebSocketMessageType.Binary) {
                            await webSocket.CloseAsync (WebSocketCloseStatus.InvalidMessageType, "Cannot accept binary frame", token).ConfigureAwait (false);
                            sessionCts.Cancel ();
                        }
                        else {
                            var size = receiveResult.Count;
                            while (!receiveResult.EndOfMessage) {
                                if (size >= receiveBuffer.Length) {
                                    await webSocket.CloseAsync (WebSocketCloseStatus.MessageTooBig, "Message too big", token).ConfigureAwait (false);
                                    return;
                                }
                                receiveResult = await webSocket.ReceiveAsync (new ArraySegment<byte>(receiveBuffer, size, receiveBuffer.Length - size), token).ConfigureAwait (false);
                                size += receiveResult.Count;
                            }
                            var receivedString = Encoding.UTF8.GetString (receiveBuffer, 0, size);

                            try {
                                // Console.WriteLine ("RECEIVED: {0}", receivedString);
                                var message = Newtonsoft.Json.JsonConvert.DeserializeObject<Message> (receivedString);
                                element.Receive (message);
                            }
                            catch (Exception ex) {
                                Error ("Failed to process received message", ex);
                            }
                        }
                    }
                }
                finally {
                    element.MessageSent -= handleElementMessageSent;
                }
            }

            void QueueStateMessages (EventTarget target)
            {
                if (target == null) return;
                foreach (var m in target.StateMessages) {
                    QueueMessage (m);
                }
            }

            void QueueMessage (Message message)
            {
                //
                // Make sure all the referenced objects have been created
                //
                if (message.MessageType == MessageType.Create) {
                    createdIds.Add (message.TargetId);
                }
                else {
                    if (!createdIds.Contains (message.TargetId)) {
                        createdIds.Add (message.TargetId);
                        QueueStateMessages (element.GetElementById (message.TargetId));
                    }
                    if (message.Value is Array a) {
                        for (var i = 0; i < a.Length; i++) {
                            // Console.WriteLine ($"A{i} = {a.GetValue(i)}");
                            if (a.GetValue (i) is EventTarget e && !createdIds.Contains (e.Id)) {
                                createdIds.Add (e.Id);
                                QueueStateMessages (e);
                            }
                        }
                    }
                }

                //
                // Add it to the queue
                //
                lock (queuedMessages) queuedMessages.Add (message);
                sendThrottle.Enabled = true;
            }

            async void TransmitQueuedMessages ()
            {
                try {
                    //
                    // Dequeue as many messages as we can
                    //
                    var messagesToSend = new List<Message> ();
                    lock (queuedMessages) {
                        messagesToSend.AddRange (queuedMessages);
                        queuedMessages.Clear ();
                    }
                    if (messagesToSend.Count == 0)
                        return;

                    //
                    // Now actually send this message
                    //
                    var json = Newtonsoft.Json.JsonConvert.SerializeObject (messagesToSend);
                    var outputBuffer = new ArraySegment<byte> (Encoding.UTF8.GetBytes (json));
                    await webSocket.SendAsync (outputBuffer, WebSocketMessageType.Text, true, token).ConfigureAwait (false);
                }
                catch (Exception ex) {                        
                    Error ("Failed to send queued messages, aborting session", ex);
                    element.MessageSent -= handleElementMessageSent;
                    sessionCts.Cancel ();
                }
            }
        }
    }
}
