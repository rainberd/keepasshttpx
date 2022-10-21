﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Net;
using System.Windows.Forms;
using System.Security.Cryptography;
using System.Threading.Tasks;
using KeePass.Plugins;
using KeePass.UI;
using KeePassLib;
using KeePassLib.Security;

using Newtonsoft.Json;
using KeePass.Util.Spr;
using Griffin.Networking.Protocol.Http;
using Griffin.Networking.Protocol.Http.Protocol;
using Griffin.Networking.Messaging;

namespace KeePassHttp
{
    internal delegate void RequestHandler(Request request, Response response, Aes aes);

    public enum CMode { ENCRYPT, DECRYPT }
    public sealed partial class KeePassHttpExt : Plugin
    {
        /// <summary>
        /// an arbitrarily generated uuid for the keepasshttp root entry
        /// </summary>
        public readonly byte[] KEEPASSHTTP_UUID = {
                0x34, 0x69, 0x7a, 0x40, 0x8a, 0x5b, 0x41, 0xc0,
                0x9f, 0x36, 0x89, 0x7d, 0x62, 0x3e, 0xcb, 0x31
                                                };

        private const int DEFAULT_NOTIFICATION_TIME = 5000;
        public const string KEEPASSHTTP_NAME = "KeePassHttp Settings";
        private const string KEEPASSHTTP_GROUP_NAME = "KeePassHttp Passwords";
        public const string ASSOCIATE_KEY_PREFIX = "AES Key: ";
        private IPluginHost host;
        private MessagingServer server;
        public const int DEFAULT_PORT = 19455;
        public const string DEFAULT_HOST = "localhost";
        /// <summary>
        /// TODO make configurable
        /// </summary>
        private const string HTTP_SCHEME = "http://";
        //private const string HTTPS_PREFIX = "https://localhost:";
        //private int HTTPS_PORT = DEFAULT_PORT + 1;
        private volatile bool stopped = false;
        Dictionary<string, RequestHandler> handlers = new Dictionary<string, RequestHandler>();

        //public string UpdateUrl = "";
        //public override string UpdateUrl { get { return "https://passifox.appspot.com/kph/latest-version.txt"; } }
        
        private readonly object _unlockOnActivitySyncRoot = new object();

        private SearchParameters MakeSearchParameters()
        {
            var p = new SearchParameters();
            p.SearchInTitles = true;
            p.RegularExpression = true;
            p.SearchInGroupNames = false;
            p.SearchInNotes = false;
            p.SearchInOther = false;
            p.SearchInPasswords = false;
            p.SearchInTags = false;
            p.SearchInUrls = true;
            p.SearchInUserNames = false;
            p.SearchInUuids = false;
            return p;
        }

        private string CryptoTransform(string input, bool base64in, bool base64out, Aes cipher, CMode mode)
        {
            byte[] bytes;
            if (base64in)
                bytes = decode64(input);
            else
                bytes = Encoding.UTF8.GetBytes(input);


            using (var c = mode == CMode.ENCRYPT ? cipher.CreateEncryptor() : cipher.CreateDecryptor()) {
            var buf = c.TransformFinalBlock(bytes, 0, bytes.Length);
            return base64out ? encode64(buf) : Encoding.UTF8.GetString(buf);
            }
        }

        private PwEntry GetConfigEntry(bool create)
        {
            var root = host.Database.RootGroup;
            var uuid = new PwUuid(KEEPASSHTTP_UUID);
            var entry = root.FindEntry(uuid, false);
            if (entry == null && create)
            {
                entry = new PwEntry(false, true);
                entry.Uuid = uuid;
                entry.Strings.Set(PwDefs.TitleField, new ProtectedString(false, KEEPASSHTTP_NAME));
                root.AddEntry(entry, true);
                UpdateUI(null);
            }
            return entry;
        }

        private int GetNotificationTime()
        {
            var time = DEFAULT_NOTIFICATION_TIME;
            var entry = GetConfigEntry(false);
            if (entry != null)
            {
                var s = entry.Strings.ReadSafe("Prompt Timeout");
                if (s != null && s.Trim() != "")
                {
                    try
                    {
                        time = Int32.Parse(s) * 1000;
                    }
                    catch { }
                }
            }

            return time;
        }

        private void ShowNotification(string text)
        {
            ShowNotification(text, null, null);
        }

        private void ShowNotification(string text, EventHandler onclick)
        {
            ShowNotification(text, onclick, null);
        }

        private void ShowNotification(string text, EventHandler onclick, EventHandler onclose)
        {
            MethodInvoker m = delegate
            {
                var notify = host.MainWindow.MainNotifyIcon;
                if (notify == null)
                    return;

                EventHandler clicked = null;
                EventHandler closed = null;

                clicked = delegate
                {
                    notify.BalloonTipClicked -= clicked;
                    notify.BalloonTipClosed -= closed;
                    if (onclick != null)
                        onclick(notify, null);
                };
                closed = delegate
                {
                    notify.BalloonTipClicked -= clicked;
                    notify.BalloonTipClosed -= closed;
                    if (onclose != null)
                        onclose(notify, null);
                };

                //notify.BalloonTipIcon = ToolTipIcon.Info;
                notify.BalloonTipTitle = "KeePassHttp";
                notify.BalloonTipText = text;
                notify.ShowBalloonTip(GetNotificationTime());
                // need to add listeners after showing, or closed is sent right away
                notify.BalloonTipClosed += closed;
                notify.BalloonTipClicked += clicked;
            };
            if (host.MainWindow.InvokeRequired)
                host.MainWindow.Invoke(m);
            else
                m.Invoke();
        }

        public override bool Initialize(IPluginHost host)
        {
            this.host = host;

            var optionsMenu = new ToolStripMenuItem("KeePassHttp Options...");
            optionsMenu.Click += OnOptions_Click;
            optionsMenu.Image = KeePassHttp.Properties.Resources.earth_lock;
            //optionsMenu.Image = global::KeePass.Properties.Resources.B16x16_File_Close;
            this.host.MainWindow.ToolsMenu.DropDownItems.Add(optionsMenu);

            try
            {
                handlers.Add(Request.TEST_ASSOCIATE, TestAssociateHandler);
                handlers.Add(Request.ASSOCIATE, AssociateHandler);
                handlers.Add(Request.GET_LOGINS, GetLoginsHandler);
                handlers.Add(Request.GET_LOGINS_COUNT, GetLoginsCountHandler);
                handlers.Add(Request.GET_ALL_LOGINS, GetAllLoginsHandler);
                handlers.Add(Request.SET_LOGIN, SetLoginHandler);
                handlers.Add(Request.GENERATE_PASSWORD, GeneratePassword);

                var configOpt = new ConfigOpt(this.host.CustomConfig);

                server = new MessagingServer(new KeePassHttpServiceFactory(_RequestHandler), new MessagingServerConfiguration(new HttpMessageFactory()));
                server.Start(new IPEndPoint(DEFAULT_HOST.Equals(configOpt.ListenerHost) ? IPAddress.Loopback : IPAddress.Parse(configOpt.ListenerHost), Convert.ToInt32(configOpt.ListenerPort)));
            }
            catch (Exception e)
            {
                MessageBox.Show(host.MainWindow,
                    "Unable to start HttpListener!\nDo you really have only one installation of KeePassHttp in your KeePass-directory?\n\n" + e,
                    "Unable to start HttpListener",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }

            return true;
        }

        void OnOptions_Click(object sender, EventArgs e)
        {
            var form = new OptionsForm(new ConfigOpt(host.CustomConfig));
            UIUtil.ShowDialogAndDestroy(form);
        }

        private JsonSerializer NewJsonSerializer()
        {
            var settings = new JsonSerializerSettings();
            settings.DefaultValueHandling = DefaultValueHandling.Ignore;
            settings.NullValueHandling = NullValueHandling.Ignore;

            return JsonSerializer.Create(settings);
        }
        private Response ProcessRequest(Request r, IResponse resp)
        {
            string hash = host.Database.RootGroup.Uuid.ToHexString() + host.Database.RecycleBinUuid.ToHexString();
            hash = getSHA1(hash);

            var response = new Response(r.RequestType, hash);

            using (var aes = new AesManaged())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                var handler = handlers[r.RequestType];
                if (handler != null)
                {
                    try
                    {
                        handler(r, response, aes);
                    }
                    catch (Exception e)
                    {
                        ShowNotification("***BUG*** " + e, (s,evt) => MessageBox.Show(host.MainWindow, e + ""));
                        response.Error = e + "";
                        resp.StatusCode = (int)HttpStatusCode.BadRequest;
                    }
                }
                else
                {
                    response.Error = "Unknown command: " + r.RequestType;
                    resp.StatusCode = (int)HttpStatusCode.BadRequest;
                }
            }

            return response;
        }

        private void _RequestHandler(IRequest req, IResponse resp)
        {
            if (stopped) return;

            var serializer = NewJsonSerializer();
            Request request = null;

            resp.StatusCode = (int)HttpStatusCode.OK;
            using (var ins = new JsonTextReader(new StreamReader(req.Body)))
            {
                try
                {
                    request = serializer.Deserialize<Request>(ins);
                }
                catch (JsonSerializationException e)
                {
                    var buffer = Encoding.UTF8.GetBytes(e + "");
                    resp.StatusCode = (int)HttpStatusCode.BadRequest;
                    resp.ContentLength = buffer.Length;
                    resp.Body.Write(buffer, 0, buffer.Length);
                } // ignore, bad request
            }

            var db = host.Database;

            var configOpt = new ConfigOpt(this.host.CustomConfig);

            if (request != null && (configOpt.UnlockDatabaseRequest || request.TriggerUnlock == "true") && !db.IsOpen) {
                var checkMainThreadAvailabilityTask = Task.Run(() =>
                        host.MainWindow.Invoke((Action)(() => {
                            /* don't do anything - we are just seeing if the thread is blocked */
                        })));

                if (!checkMainThreadAvailabilityTask.Wait(1000)) {
                    return; // the main thread seems to be locked, it would be safe not to try unlocking databases
                    // ↑ For more details, see #19 "KeePass Locks Up in combination of IOProtocolExt and KeyAgent when trying to Synchronise a database"
                }

                lock (_unlockOnActivitySyncRoot) {
                    // ↑ protection from concurrent requests

                    host.MainWindow.Invoke((Action)(() => {
                        var document = host.MainWindow.DocumentManager.ActiveDocument;
                        if (host.MainWindow.IsFileLocked(document)) {
                            host.MainWindow.OpenDatabase(document.LockedIoc, null, false);
                        }
                        /*
                        foreach (var document in host.MainWindow.DocumentManager.Documents) {
                            if (host.MainWindow.IsFileLocked(document)) {
                                host.MainWindow.OpenDatabase(document.LockedIoc, null, false);
                            }
                        }
                        */
                    }));
                }
            }

            if (request != null && db.IsOpen)
            {
                Response response = null;
                if (request != null)
                    response = ProcessRequest(request, resp);

                resp.ContentType = "application/json";
                var writer = new StringWriter();
                if (response != null)
                {
                    serializer.Serialize(writer, response);
                    var buffer = Encoding.UTF8.GetBytes(writer.ToString());
                    resp.ContentLength = buffer.Length;
                    resp.Body.Write(buffer, 0, buffer.Length);
                }
            }
            else
            {
                resp.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            }
        }

        public override void Terminate()
        {
            stopped = true;

            server.Stop();
        }

        private void UpdateUI(PwGroup group)
        {
            var win = host.MainWindow;
            if (group == null) group = host.Database.RootGroup;
            var f = (MethodInvoker) delegate {
                win.UpdateUI(false, null, true, group, true, null, true);
            };
            if (win.InvokeRequired)
                win.Invoke(f);
            else
                f.Invoke();
        }

        internal string[] GetUserPass(PwEntry entry)
        {
            return GetUserPass(new PwEntryDatabase(entry, host.Database));
        }

        internal string[] GetUserPass(PwEntryDatabase entryDatabase)
        {
            // follow references
            SprContext ctx = new SprContext(entryDatabase.entry, entryDatabase.database,
                    SprCompileFlags.All, false, false);
            string user = SprEngine.Compile(
                    entryDatabase.entry.Strings.ReadSafe(PwDefs.UserNameField), ctx);
            string pass = SprEngine.Compile(
                    entryDatabase.entry.Strings.ReadSafe(PwDefs.PasswordField), ctx);
            var f = (MethodInvoker)delegate
            {
                // apparently, SprEngine.Compile might modify the database
                host.MainWindow.UpdateUI(false, null, false, null, false, null, false);
            };
            if (host.MainWindow.InvokeRequired)
                host.MainWindow.Invoke(f);
            else
                f.Invoke();

            return new string[] { user, pass };
        }

        /// <summary>
        /// Liefert den SHA1 Hash 
        /// </summary>
        /// <param name="input">Eingabestring</param>
        /// <returns>SHA1 Hash der Eingabestrings</returns>
        private string getSHA1(string input)
        {
            //Umwandlung des Eingastring in den SHA1 Hash
            System.Security.Cryptography.SHA1 sha1 = new System.Security.Cryptography.SHA1CryptoServiceProvider();
            byte[] textToHash = Encoding.Default.GetBytes(input);
            byte[] result = sha1.ComputeHash(textToHash);

            //SHA1 Hash in String konvertieren
            System.Text.StringBuilder s = new System.Text.StringBuilder();
            foreach (byte b in result)
            {
                s.Append(b.ToString("x2").ToLower());
            }

            return s.ToString();
        }
    }
}
