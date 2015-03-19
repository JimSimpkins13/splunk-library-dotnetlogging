using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Splunk.Logging
{
    public class TestHttpInput
    {
        private const string Uri = "http://localhost:5555";
        private const string HttpInputUrl = Uri + "/services/receivers/token/";

        // A dummy http input server
        private class HttpServer
        {
            private readonly HttpListener listener = new HttpListener();

            public class Response
            {
                public int Code = 200;
                public string Context = "{\"text\":\"Success\",\"code\":0}";
            }
            public Func<string, dynamic, Response> Method { get; set; }

            public HttpServer(string url)
            {
                listener.Prefixes.Add(url);
                listener.Start();
            }
 
            public void Run()
            {
                ThreadPool.QueueUserWorkItem((wi) =>
                {
                    try
                    {
                        while (listener.IsListening)
                        {
                            ThreadPool.QueueUserWorkItem((obj) =>
                            {
                                var context = obj as HttpListenerContext;
                                try
                                {
                                    string input = new StreamReader(context.Request.InputStream).ReadToEnd();
                                    string authorization = context.Request.Headers.Get("Authorization");                                    
                                    dynamic jobj = JObject.Parse(input);
                                    Response response = Method(authorization, jobj);
                                    context.Response.StatusCode = response.Code;
                                    byte[] buf = Encoding.UTF8.GetBytes(response.Context);
                                    context.Response.ContentLength64 = buf.Length;
                                    context.Response.OutputStream.Write(buf, 0, buf.Length);                                    
                                }
                                catch (Exception e) 
                                {
                                    Assert.True(false, e.ToString());   
                                } 
                                finally
                                {
                                    // always close the stream
                                    context.Response.OutputStream.Close();
                                }
                            }, listener.GetContext());
                        }
                    }
                    catch { } // suppress any exceptions
                });
            }

            public void Stop()
            {
                listener.Stop();
                listener.Close();
            }
        }

        private HttpServer server = new HttpServer(HttpInputUrl);
 
        public TestHttpInput()
        {
            server.Method = (auth, input) => { return new HttpServer.Response(); };
            server.Run();            
        }

        [Trait("integration-tests", "Splunk.Logging.HttpInputTraceListener")]
        [Fact]
        public void HttpInputTraceListener()
        {
            // setup the logger
            var trace = new TraceSource("HttpInputLogger");
            trace.Switch.Level = SourceLevels.All;
            var meta = new Dictionary<string, string>();
            meta["index"] = "main";
            meta["source"] = "localhost";
            meta["sourcetype"] = "log";
            trace.Listeners.Add(new HttpInputTraceListener(Uri, "TOKEN", 0, 0, 0, 0, meta));

            // test authentication
            server.Method = (auth, input) => 
            {
                Assert.True(auth == "Splunk TOKEN", "wrong authentication");
                return new HttpServer.Response(); 
            };
            trace.TraceEvent(TraceEventType.Information, 1, "info");
            Sleep();

            // test metadata
            ulong now =
                (ulong)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            server.Method = (auth, input) =>
            {
                Assert.True(input.index.Value == "main");
                Assert.True(input.source.Value == "localhost");
                Assert.True(input.sourcetype.Value == "log");
                // check that timestamp is correct
                ulong time = ulong.Parse(input.time.Value);
                Assert.True(time - now <= 1);
                return new HttpServer.Response();
            };
            trace.TraceEvent(TraceEventType.Information, 1, "info");
            Sleep();

            // test event info
            server.Method = (auth, input) =>
            {
                Assert.True(input["event"].id.Value == "123");
                Assert.True(input["event"].severity.Value == "Error");
                Assert.True(input["event"].message.Value == "Test error");
                return new HttpServer.Response();
            };
            trace.TraceEvent(TraceEventType.Error, 123, "Test error");
            Sleep();

            server.Stop();
        }

        private void Sleep()
        {
            // logger and server are async thus we need short delays between individual tests
            Thread.Sleep(500); 
        }
    }
}
