using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;

namespace RouteLens
{
    public static class Flags
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Image> Images = new Dictionary<string, Image>();
        private static readonly Dictionary<string, DateTime> Attempts = new Dictionary<string, DateTime>();
        private static readonly Semaphore Slots = new Semaphore(2, 2);
        public static volatile bool NetworkEnabled = true;

        public static bool ValidCode(string code)
        { return code != null && code.Length == 2 && Char.IsLetter(code[0]) && Char.IsLetter(code[1]) && code[0] < 128 && code[1] < 128; }

        public static Image Get(string code)
        {
            if (!ValidCode(code)) return null;
            code = code.ToLowerInvariant();
            lock (Gate)
            {
                Image found;
                if (Images.TryGetValue(code, out found)) return found;
                Stream embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Flags." + code + ".png");
                if (embedded != null)
                {
                    using (embedded)
                    using (Image raw = Image.FromStream(embedded)) found = new Bitmap(raw);
                    Images[code] = found;
                    return found;
                }
                if (!NetworkEnabled) return null;
                DateTime previous;
                if (Attempts.TryGetValue(code, out previous) && DateTime.UtcNow - previous < TimeSpan.FromMinutes(10)) return null;
                Attempts[code] = DateTime.UtcNow;
                string requested = code;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Slots.WaitOne();
                    try
                    {
                        if (!NetworkEnabled) return;
                        byte[] bytes = NetFetch.Bytes("https://flagcdn.com/w40/" + requested + ".png");
                        using (MemoryStream memory = new MemoryStream(bytes))
                        using (Image raw = Image.FromStream(memory))
                        {
                            Image flag = new Bitmap(raw);
                            lock (Gate) Images[requested] = flag;
                        }
                    }
                    catch (Exception) { /* Country name and code remain visible when CDN is unavailable. */ }
                    finally { Slots.Release(); }
                });
                return null;
            }
        }
    }
}
