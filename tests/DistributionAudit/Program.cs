using System.Collections;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Static file inspection only. Never loads or executes the product assembly.
try
{
    if (args.Length is < 4 or > 5)
        throw new ArgumentException("Usage: DistributionAudit <bundle> <source-root> <apphost> <report-json> [reference-bundle]");
    var bundle = Path.GetFullPath(args[0]);
    var source = Path.GetFullPath(args[1]);
    var host = Path.GetFullPath(args[2]);
    var reportPath = Path.GetFullPath(args[3]);
    var checks = 0;
    void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
    string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    string FileHash(string file) => Hash(File.ReadAllBytes(file));
    var expected = new[] {
        "ToolboxMatrix.exe", "ToolboxMatrix.dll", "ToolboxMatrix.deps.json", "ToolboxMatrix.runtimeconfig.json",
        "tlbxm.cmd", "LICENSE", "ARTWORK_NOTICE.md", "THIRD_PARTY_NOTICES.md", "README.txt", "CANDIDATE.json",
        "docs/LICENSING.md", "docs/licenses/dotnet-host-10.0.13-LICENSE.txt",
        "docs/licenses/dotnet-host-10.0.13-THIRD-PARTY-NOTICES.txt" };
    var actual = Directory.GetFiles(bundle, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(bundle, p).Replace('\\', '/')).Order().ToArray();
    Check(actual.SequenceEqual(expected.Order()), "Distribution contains unexpected or missing files.");
    var forbidden = new Dictionary<string, string> {
        ["user-profile-path"] = @"(?i)[a-z]:[\\/]+Users[\\/]+",
        ["UNC-path"] = @"\\\\[A-Za-z0-9_-]+\\(?:Users|home|profiles|share)\\",
        ["private-key"] = @"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----",
        ["GitHub-token"] = @"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})",
        ["OpenAI-key"] = @"sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{32,}",
        ["AWS-access-key"] = @"(?:AKIA|ASIA)[A-Z0-9]{16}"
    };
    var findings = new List<object>();
    void Scan(byte[] bytes, string file)
    {
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
            foreach (var pattern in forbidden)
                if (Regex.IsMatch(encoding.GetString(bytes), pattern.Value))
                    findings.Add(new { File = file, Rule = pattern.Key, Encoding = encoding.WebName });
    }
    foreach (var file in actual) Scan(File.ReadAllBytes(Path.Combine(bundle, file)), file);
    Check(findings.Count == 0, "A path or secret pattern was found. Values are not logged.");
    var dllPath = Path.Combine(bundle, "ToolboxMatrix.dll");
    var exePath = Path.Combine(bundle, "ToolboxMatrix.exe");
    using var dllStream = File.OpenRead(dllPath);
    using var dll = new PEReader(dllStream);
    Check(dll.HasMetadata, "Application DLL has no managed metadata.");
    var metadata = dll.GetMetadataReader();
    Check(metadata.GetAssemblyDefinition().Version == new Version(1,0,0,0), "Unexpected V1 assembly version.");
    var debug = dll.ReadDebugDirectory();
    Check(!debug.Any(d => d.Type is DebugDirectoryEntryType.CodeView or DebugDirectoryEntryType.EmbeddedPortablePdb),
        "Application DLL still contains PDB/CodeView metadata.");
    using var exeStream = File.OpenRead(exePath);
    using var exe = new PEReader(exeStream);
    Check(exe.PEHeaders.CoffHeader.Machine == Machine.Amd64, "Application host is not x64.");
    Check(exe.PEHeaders.PEHeader!.Subsystem == Subsystem.WindowsGui, "Unexpected host subsystem.");
    byte[] CodeSection(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new PEReader(stream);
        var section = reader.PEHeaders.SectionHeaders.Single(s => s.Name == ".text");
        return reader.GetSectionData(section.VirtualAddress).GetContent(0, section.VirtualSize).ToArray();
    }
    Check(Hash(CodeSection(host)) == Hash(CodeSection(exePath)), "Packaged native host code differs from the installed host.");
    Dictionary<string, byte[]> ManagedResources(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new PEReader(stream);
        var md = reader.GetMetadataReader();
        var directory = reader.PEHeaders.CorHeader!.ResourcesDirectory;
        var block = reader.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size).ToArray();
        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var handle in md.ManifestResources)
        {
            var resource = md.GetManifestResource(handle);
            if (!resource.Implementation.IsNil) throw new InvalidOperationException("External assembly resource.");
            var name = md.GetString(resource.Name);
            var offset = checked((int)resource.Offset);
            var length = BitConverter.ToInt32(block, offset);
            var bytes = block.AsSpan(offset + 4, length).ToArray();
            Scan(bytes, name);
            if (name.EndsWith(".resources", StringComparison.Ordinal))
            {
                using var resourceStream = new MemoryStream(bytes);
                using var values = new ResourceReader(resourceStream);
                var enumerator = values.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var key = (string)enumerator.Key;
                    values.GetResourceData(key, out var type, out var data);
                    // Raw resource access avoids deserializing or instantiating WPF objects.
                    var payload = type is "ResourceTypeCode.Stream" or "ResourceTypeCode.ByteArray"
                        ? data.AsSpan(4, BitConverter.ToInt32(data, 0)).ToArray() : data;
                    Scan(payload, name + "/" + key);
                    found.Add(name + "/" + key, payload);
                }
            }
            else found.Add(name, bytes);
        }
        return found;
    }
    var resources = ManagedResources(dllPath);
    Check(resources.Count > 0, "No managed resources found.");
    Check(!resources.Keys.Any(k => k.Contains("logos/", StringComparison.OrdinalIgnoreCase)), "Excluded brand resources are embedded.");
    var icon = resources.Single(kv => Uri.UnescapeDataString(kv.Key).EndsWith("/assets/toolbox matrix.ico", StringComparison.OrdinalIgnoreCase));
    Check(Hash(icon.Value) == FileHash(Path.Combine(source, "src/ToolboxMatrix/Assets/Toolbox Matrix.ico")),
        "Embedded icon differs from the source icon.");
    Check(Hash(icon.Value) == "A73C0EDFD9461B4636B4080A3288327561B9E6B23984D64D06755A65A2AF4D8A",
        "Original v02 icon hash changed.");
    Dictionary<string, string> NativeIcons(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new PEReader(stream);
        var resourceRva = reader.PEHeaders.PEHeader!.ResourceTableDirectory.RelativeVirtualAddress;
        var resourceSize = reader.PEHeaders.PEHeader.ResourceTableDirectory.Size;
        var block = reader.GetSectionData(resourceRva).GetContent(0, resourceSize).ToArray();
        var result = new Dictionary<string, string>();
        void Walk(int offset, string prefix, int depth)
        {
            if (depth > 4) throw new InvalidOperationException("Invalid resource depth.");
            var count = BitConverter.ToUInt16(block, offset + 12) + BitConverter.ToUInt16(block, offset + 14);
            for (var i = 0; i < count; i++)
            {
                var entry = offset + 16 + i * 8;
                var name = BitConverter.ToUInt32(block, entry);
                var target = BitConverter.ToUInt32(block, entry + 4);
                var label = (name & 0x80000000) == 0 ? name.ToString() : "named";
                var path = prefix.Length == 0 ? label : prefix + "/" + label;
                if (depth == 0 && label is not ("3" or "14")) continue;
                if ((target & 0x80000000) != 0) Walk((int)(target & 0x7fffffff), path, depth + 1);
                else
                {
                    var rva = BitConverter.ToInt32(block, (int)target);
                    var size = BitConverter.ToInt32(block, (int)target + 4);
                    result.Add(path, Hash(reader.GetSectionData(rva).GetContent(0, size).ToArray()));
                }
            }
        }
        Walk(0, "", 0);
        return result;
    }
    var nativeIcons = NativeIcons(exePath);
    Check(nativeIcons.Count >= 11, "Expected multi-frame native icon resources are missing.");
    bool? referenceResourcesMatch = null;
    bool? referenceNativeIconsMatch = null;
    if (args.Length == 5)
    {
        var reference = ManagedResources(Path.Combine(args[4], "ToolboxMatrix.dll"));
        referenceResourcesMatch = resources.Count == reference.Count &&
            resources.All(kv => reference.TryGetValue(kv.Key, out var value) && Hash(value) == Hash(kv.Value));
        Check(referenceResourcesMatch == true, "Managed resources differ from the original approved candidate.");
        var oldIcons = NativeIcons(Path.Combine(args[4], "ToolboxMatrix.exe"));
        referenceNativeIconsMatch = nativeIcons.Count == oldIcons.Count &&
            nativeIcons.All(kv => oldIcons.TryGetValue(kv.Key, out var value) && value == kv.Value);
        Check(referenceNativeIconsMatch == true, "Native icon resources differ from the original approved candidate.");
    }
    var notices = new Dictionary<string,string> {
        ["dotnet-host-10.0.13-LICENSE.txt"] = "D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B",
        ["dotnet-host-10.0.13-THIRD-PARTY-NOTICES.txt"] = "6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21"
    };
    foreach (var notice in notices)
        Check(FileHash(Path.Combine(bundle, "docs/licenses", notice.Key)) == notice.Value, "Upstream notice changed.");
    Check(FileHash(Path.Combine(bundle, "tlbxm.cmd")) == FileHash(Path.Combine(source, "scripts/tlbxm.cmd")), "Launcher changed.");
    using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundle, "ToolboxMatrix.runtimeconfig.json")));
    var frameworks = runtime.RootElement.GetProperty("runtimeOptions").GetProperty("frameworks")
        .EnumerateArray().Select(e => e.GetProperty("name").GetString()).Order().ToArray();
    Check(frameworks.SequenceEqual(new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" }), "Unexpected runtime dependencies.");
    using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundle, "ToolboxMatrix.deps.json")));
    Check(deps.RootElement.GetProperty("libraries").EnumerateObject().All(p => p.Value.GetProperty("type").GetString() == "project"),
        "Unexpected external package payload dependency.");
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundle, "CANDIDATE.json")));
    foreach (var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
        Check(FileHash(Path.Combine(bundle, file.GetProperty("Path").GetString()!)) == file.GetProperty("SHA256").GetString(),
            "Candidate manifest mismatch.");
    Check(findings.Count == 0, "Embedded resource contains a forbidden pattern.");
    var report = new {
        Status = "PASS", Checks = checks, Files = actual.Length, ScanFindings = findings,
        ScanEncodings = new[] { "UTF-8/ASCII", "UTF-16LE", "UTF-16BE" },
        ManagedDebugEntries = debug.Select(d => d.Type.ToString()).ToArray(),
        ManagedResources = resources.Select(kv => new { Resource = kv.Key, SHA256 = Hash(kv.Value) }).ToArray(),
        NativeIconResources = nativeIcons, HostCodeSHA256 = Hash(CodeSection(host)),
        OriginalManagedResourcesMatch = referenceResourcesMatch, OriginalNativeIconsMatch = referenceNativeIconsMatch,
        Limit = "Static evidence only. No app/UI/provider/installer execution; no legal clearance guarantee."
    };
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"DistributionAudit: {checks} checks passed; {actual.Length} files; no forbidden path/secret patterns.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("DistributionAudit FAILED: " + error.Message);
    return 1;
}
