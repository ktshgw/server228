using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

internal static class Program
{
    private static byte[] moduleBytes = Encoding.UTF8.GetBytes("verified SOMS module cache test fixture");
    private static int requests;
    private static int checks;

    [STAThread]
    private static void Main(string[] args)
    {
        string root = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(root);
        Assembly switcher = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Type formType = switcher.GetType("SomsSwitcher.MainForm", true);
        object form = FormatterServices.GetUninitializedObject(formType);
        MethodInfo download = formType.GetMethod("DownloadModule", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new Type[] { typeof(Dictionary<string, object>), typeof(string) }, null);
        check(download != null, "Cache method not found in built switcher");
        string manifestUrl = (string)formType.GetField("ManifestUrl", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
        check(WebRequest.RegisterPrefix(new Uri(new Uri(manifestUrl), "/soms-module-cache-check/").AbsoluteUri, new FixtureCreator()),
            "Could not register isolated in-process download fixture");
        string hash;
        using (SHA256 sha = SHA256.Create())
            hash = BitConverter.ToString(sha.ComputeHash(moduleBytes)).Replace("-", "").ToLowerInvariant();

        Func<string, string> run = delegate(string filename)
        {
            var module = new Dictionary<string, object>
            {
                { "url", "/soms-module-cache-check/" + filename },
                { "filename", filename },
                { "size", moduleBytes.Length },
                { "sha256", hash },
            };
            return (string)download.Invoke(form, new object[] { module, root });
        };
        Func<string, string> pathFor = delegate(string filename) { return Path.Combine(root, hash.Substring(0, 16) + "-" + filename); };

        string valid = pathFor("valid.dll");
        File.WriteAllBytes(valid, moduleBytes);
        check(run("valid.dll") == valid, "Valid cache path was not reused");
        check(requests == 0, "Valid cache unnecessarily downloaded again");

        string corrupt = pathFor("corrupt.dll");
        File.WriteAllText(corrupt, "corrupted module");
        check(run("corrupt.dll") == corrupt, "Corrupt cache repair returned wrong path");
        check(File.ReadAllText(corrupt) == Encoding.UTF8.GetString(moduleBytes), "Corrupt cache was returned without replacement");
        check(requests == 1, "Corrupt cache was not downloaded exactly once");
        check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "Repair left temporary files behind");

        string locked = pathFor("locked.dll");
        File.WriteAllText(locked, "locked corrupted module");
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool rejected = false;
            try
            {
                run("locked.dll");
            }
            catch (TargetInvocationException ex)
            {
                rejected = ex.InnerException is IOException && ex.InnerException.Message.Contains("Запуск отменён");
            }
            check(rejected, "Locked damaged cache was returned or did not report explicit cancellation");
            check(File.ReadAllText(locked) == "locked corrupted module", "Locked cache changed despite rejected update");
        }
        check(requests == 2, "Locked-cache scenario did not exercise the replacement path");
        check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "Failed replacement left temporary files behind");
        check(run("locked.dll") == locked && File.ReadAllText(locked) == Encoding.UTF8.GetString(moduleBytes),
            "Retry after releasing the lock did not repair cache");

        string missing = pathFor("missing.dll");
        check(run("missing.dll") == missing && File.ReadAllText(missing) == Encoding.UTF8.GetString(moduleBytes),
            "First download could not populate missing cache");
        check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "Successful operations left temporary files behind");
        // Use the actual published DLL that crossed the old 16 MiB limit.
        moduleBytes = File.ReadAllBytes(Path.GetFullPath(args[2]));
        check(moduleBytes.Length > 16 * 1024 * 1024, "Regression fixture must exceed the old module limit");
        using (SHA256 sha = SHA256.Create())
            hash = BitConverter.ToString(sha.ComputeHash(moduleBytes)).Replace("-", "").ToLowerInvariant();
        string large = run("large.dll");
        check(new FileInfo(large).Length == moduleBytes.Length, "Current client DLL was not downloaded completely");
        int beforeCache = requests;
        check(run("large.dll") == large && requests == beforeCache, "Verified large DLL was not reused");

        int limit = (int)formType.GetField("MaxModuleBytes", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
        var invalid = new Dictionary<string, object>
        {
            { "url", "/soms-module-cache-check/invalid.dll" }, { "filename", "invalid.dll" },
            { "size", 0 }, { "sha256", hash },
        };
        reject(download, form, root, invalid, "размер");
        invalid["size"] = -1;
        reject(download, form, root, invalid, "размер");
        invalid["size"] = limit + 1;
        reject(download, form, root, invalid, "лимит");
        check(requests == beforeCache, "Invalid sizes initiated a download");
        invalid["size"] = moduleBytes.Length + 1;
        reject(download, form, root, invalid, "манифестом");
        invalid["size"] = moduleBytes.Length;
        invalid["sha256"] = new string('0', 64);
        reject(download, form, root, invalid, "SHA-256");
        // A valid hash in cache cannot override a contradictory manifest size.
        invalid["filename"] = "large.dll";
        invalid["sha256"] = hash;
        invalid["size"] = moduleBytes.Length + 1;
        reject(download, form, root, invalid, "манифестом");
        check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "Invalid downloads left temporary files");
        Console.WriteLine("PASS: " + checks + " checks against built SOMS-switcher.exe; cache repair, actual >16 MiB module, invalid sizes and SHA-256 rejection. No network or real user cache used.");
    }

    private static void reject(MethodInfo download, object form, string root, Dictionary<string, object> module, string message)
    {
        bool rejected = false;
        try { download.Invoke(form, new object[] { module, root }); }
        catch (TargetInvocationException ex)
        {
            rejected = ex.InnerException is InvalidDataException && ex.InnerException.Message.Contains(message);
        }
        check(rejected, "Invalid module accepted or unexpected error: " + message);
    }

    private static void check(bool value, string description)
    {
        if (!value) throw new Exception(description);
        checks++;
    }

    private sealed class FixtureCreator : IWebRequestCreate
    {
        public WebRequest Create(Uri uri) { return new FixtureRequest(uri); }
    }

    private sealed class FixtureRequest : WebRequest
    {
        private readonly Uri uri;
        private WebHeaderCollection headers = new WebHeaderCollection();
        public FixtureRequest(Uri uri) { this.uri = uri; }
        public override WebHeaderCollection Headers { get { return headers; } set { headers = value; } }
        public override string Method { get; set; }
        public override ICredentials Credentials { get; set; }
        public override IWebProxy Proxy { get; set; }
        public override int Timeout { get; set; }
        public override long ContentLength { get; set; }
        public override WebResponse GetResponse()
        {
            requests++;
            return new FixtureResponse(uri);
        }
    }

    private sealed class FixtureResponse : WebResponse
    {
        private readonly Uri uri;
        public FixtureResponse(Uri uri) { this.uri = uri; }
        public override Uri ResponseUri { get { return uri; } }
        public override long ContentLength { get { return moduleBytes.Length; } set { throw new NotSupportedException(); } }
        public override WebHeaderCollection Headers { get { return new WebHeaderCollection(); } }
        public override Stream GetResponseStream() { return new MemoryStream(moduleBytes, false); }
    }
}
