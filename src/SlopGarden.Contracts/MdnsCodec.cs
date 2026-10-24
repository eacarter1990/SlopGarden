using System.Net;
using System.Text;

namespace SlopGarden.Contracts;

/// <summary>One SlopGarden service learned from an mDNS packet. <see cref="Url"/> is set for a server advertisement; <see cref="HardwareId"/> for a client.</summary>
public sealed record MdnsServiceInfo(
    string ServiceType,
    string Instance,
    string Host,
    int Port,
    IPAddress? Address,
    string? HardwareId,
    string? Version,
    string? Name,
    string? Url);

/// <summary>Minimal DNS codec for the two SlopGarden service types. Names may be compressed on input; output is uncompressed.</summary>
public static class MdnsCodec
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeSrv = 33;
    public const ushort ClassIn = 1;

    public static byte[] Query(string serviceType) => Message(0, 0, [Question(serviceType, TypePtr)], []);

    public static byte[] AnnounceClient(string instanceLabel, int port, IPAddress address, string hardwareId, string version, string name)
    {
        var instance = $"{Sanitize(instanceLabel)}.{Discovery.MdnsClientService}";
        var host = $"{Sanitize(instanceLabel)}.local";
        var records = new List<DnsRecord>
        {
            new(Discovery.MdnsClientService, TypePtr, NameBytes(instance)),
            new(instance, TypeSrv, SrvBytes(port, host)),
            new(instance, TypeTxt, Txt(["hw=" + hardwareId, "ver=" + version, "name=" + name])),
            new(host, TypeA, address.GetAddressBytes()),
        };
        return Message(0, 0x8400, [], records);
    }

    public static byte[] AnnounceServer(string instanceLabel, int port, IPAddress address, string url)
    {
        var instance = $"{Sanitize(instanceLabel)}.{Discovery.MdnsServerService}";
        var host = $"{Sanitize(instanceLabel)}.local";
        var records = new List<DnsRecord>
        {
            new(Discovery.MdnsServerService, TypePtr, NameBytes(instance)),
            new(instance, TypeSrv, SrvBytes(port, host)),
            new(instance, TypeTxt, Txt(["url=" + url])),
            new(host, TypeA, address.GetAddressBytes()),
        };
        return Message(0, 0x8400, [], records);
    }

    public static bool IsQueryFor(byte[] packet, string serviceType)
    {
        if (!TryRead(packet, out var msg) || !msg.IsQuery) return false;
        return msg.Questions.Any(q => string.Equals(q, serviceType, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryRead(byte[] packet, out MdnsMessage message)
    {
        message = new MdnsMessage(false, [], []);
        try
        {
            if (packet.Length < 12) return false;
            var r = new Reader(packet);
            _ = r.U16();
            var flags = r.U16();
            var qd = r.U16();
            var an = r.U16();
            var ns = r.U16();
            var ar = r.U16();
            var questions = new List<string>();
            for (var i = 0; i < qd; i++)
            {
                questions.Add(r.Name());
                _ = r.U16();
                _ = r.U16();
            }
            var records = new List<DnsRecord>();
            for (var i = 0; i < an + ns + ar; i++)
            {
                var name = r.Name();
                var type = r.U16();
                _ = r.U16();
                _ = r.U32();
                var len = r.U16();
                records.Add(new DnsRecord(name, type, r.Take(len)));
            }
            message = new MdnsMessage((flags & 0x8000) == 0, questions, Services(packet, records));
            return true;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    public static string Sanitize(string label)
    {
        var chars = label.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' ? char.ToLowerInvariant(c) : '-').ToArray();
        var s = new string(chars).Trim('-');
        if (s.Length == 0) s = "slopgarden";
        return s.Length > 40 ? s[..40].Trim('-') : s;
    }

    private static IReadOnlyList<MdnsServiceInfo> Services(byte[] packet, List<DnsRecord> records)
    {
        var list = new List<MdnsServiceInfo>();
        foreach (var ptr in records.Where(r => r.Type == TypePtr))
        {
            var instance = new Reader(packet).NameAt(ptr.Data, 0, packet);
            var srv = records.FirstOrDefault(r => r.Type == TypeSrv && NameEquals(r.Name, instance));
            var txt = records.FirstOrDefault(r => r.Type == TypeTxt && NameEquals(r.Name, instance));
            if (srv is null) continue;
            var (port, host) = ReadSrv(packet, srv.Data);
            var a = records.FirstOrDefault(r => r.Type == TypeA && NameEquals(r.Name, host) && r.Data.Length == 4);
            var fields = ReadTxt(txt?.Data ?? []);
            fields.TryGetValue("hw", out var hw);
            fields.TryGetValue("ver", out var ver);
            fields.TryGetValue("name", out var display);
            fields.TryGetValue("url", out var url);
            IPAddress? address = a is null ? null : new IPAddress(a.Data);
            list.Add(new MdnsServiceInfo(ptr.Name, instance, host, port, address, hw, ver, display, url));
        }
        return list;
    }

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static (int Port, string Host) ReadSrv(byte[] packet, byte[] data)
    {
        var port = (data[4] << 8) | data[5];
        var host = new Reader(packet).NameAt(data, 6, packet);
        return (port, host);
    }

    private static Dictionary<string, string> ReadTxt(byte[] data)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < data.Length)
        {
            var len = data[i++];
            if (i + len > data.Length) break;
            var text = Encoding.UTF8.GetString(data, i, len);
            i += len;
            var eq = text.IndexOf('=');
            if (eq > 0) map[text[..eq]] = text[(eq + 1)..];
        }
        return map;
    }

    private static byte[] Question(string name, ushort type)
    {
        using var ms = new MemoryStream();
        WriteName(ms, name);
        U16(ms, type);
        U16(ms, ClassIn);
        return ms.ToArray();
    }

    private static byte[] Message(ushort id, ushort flags, IReadOnlyList<byte[]> questions, IReadOnlyList<DnsRecord> answers)
    {
        using var ms = new MemoryStream();
        U16(ms, id);
        U16(ms, flags);
        U16(ms, (ushort)questions.Count);
        U16(ms, (ushort)answers.Count);
        U16(ms, 0);
        U16(ms, 0);
        foreach (var q in questions) ms.Write(q);
        foreach (var a in answers)
        {
            WriteName(ms, a.Name);
            U16(ms, a.Type);
            U16(ms, (ushort)(ClassIn | 0x8000));
            U32(ms, 120);
            U16(ms, (ushort)a.Data.Length);
            ms.Write(a.Data);
        }
        return ms.ToArray();
    }

    private static byte[] NameBytes(string name)
    {
        using var ms = new MemoryStream();
        WriteName(ms, name);
        return ms.ToArray();
    }

    private static byte[] SrvBytes(int port, string host)
    {
        using var ms = new MemoryStream();
        U16(ms, 0);
        U16(ms, 0);
        U16(ms, (ushort)port);
        WriteName(ms, host);
        return ms.ToArray();
    }

    private static byte[] Txt(IEnumerable<string> fields)
    {
        using var ms = new MemoryStream();
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            if (bytes.Length > 255) bytes = bytes[..255];
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        return ms.ToArray();
    }

    private static void WriteName(Stream ms, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
    }

    private static void U16(Stream ms, ushort v)
    {
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)v);
    }

    private static void U32(Stream ms, uint v)
    {
        ms.WriteByte((byte)(v >> 24));
        ms.WriteByte((byte)(v >> 16));
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)v);
    }

    private sealed record DnsRecord(string Name, ushort Type, byte[] Data);

    private sealed class Reader(byte[] packet, int start = 0)
    {
        private int _i = start;
        public ushort U16() { var v = (ushort)((packet[_i] << 8) | packet[_i + 1]); _i += 2; return v; }
        public uint U32() { var v = (uint)((packet[_i] << 24) | (packet[_i + 1] << 16) | (packet[_i + 2] << 8) | packet[_i + 3]); _i += 4; return v; }
        public byte[] Take(int n) { var s = packet.AsSpan(_i, n).ToArray(); _i += n; return s; }
        public string Name() => ReadName(packet, ref _i);

        public string NameAt(byte[] data, int offset, byte[] message)
        {
            // SRV/PTR rdata may point back into the original message via compression.
            if (offset < data.Length && (data[offset] & 0xC0) == 0xC0 && data.Length - offset >= 2)
            {
                var ptr = ((data[offset] & 0x3F) << 8) | data[offset + 1];
                var i = ptr;
                return ReadName(message, ref i);
            }
            var combined = new byte[message.Length + data.Length];
            message.CopyTo(combined, 0);
            data.CopyTo(combined, message.Length);
            var at = message.Length + offset;
            return ReadName(combined, ref at);
        }

        private static string ReadName(byte[] buf, ref int i)
        {
            var labels = new List<string>();
            var jumped = false;
            var end = i;
            var guard = 0;
            while (i < buf.Length && guard++ < 128)
            {
                var len = buf[i];
                if (len == 0)
                {
                    i++;
                    if (!jumped) end = i;
                    break;
                }
                if ((len & 0xC0) == 0xC0)
                {
                    var ptr = ((len & 0x3F) << 8) | buf[i + 1];
                    i += 2;
                    if (!jumped) end = i;
                    i = ptr;
                    jumped = true;
                    continue;
                }
                i++;
                labels.Add(Encoding.UTF8.GetString(buf, i, len));
                i += len;
            }
            if (jumped) i = end;
            return string.Join('.', labels);
        }
    }
}

public sealed record MdnsMessage(bool IsQuery, IReadOnlyList<string> Questions, IReadOnlyList<MdnsServiceInfo> Services);
