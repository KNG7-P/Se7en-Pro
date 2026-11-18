using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Se7enPro.Services.Foxy;

public static class HpackCodec
{
    private sealed class HuffmanNode
    {
        public int Symbol = -1;
        public HuffmanNode? Left;
        public HuffmanNode? Right;
    }

    private static readonly string[] StaticTableNames =
    [
        "", ":authority", ":method", ":method", ":path", ":path", ":scheme", ":scheme", ":status", ":status",
        ":status", ":status", ":status", ":status", ":status", "accept-charset", "accept-encoding", "accept-language",
        "accept-ranges", "accept", "access-control-allow-origin", "age", "allow", "authorization", "cache-control",
        "content-disposition", "content-encoding", "content-language", "content-length", "content-location",
        "content-range", "content-type", "cookie", "date", "etag", "expect", "expires", "from", "host", "if-match",
        "if-modified-since", "if-none-match", "if-range", "if-unmodified-since", "last-modified", "link", "location",
        "max-forwards", "proxy-authenticate", "proxy-authorization", "range", "referer", "refresh", "retry-after",
        "server", "set-cookie", "strict-transport-security", "transfer-encoding", "user-agent", "vary",
        "via", "www-authenticate",
    ];

    private static readonly (uint Code, int Bits)[] HuffmanCodeTable =
    [
        (8184u, 13), (8388568u, 23), (268435426u, 28), (268435427u, 28), (268435428u, 28), (268435429u, 28),
        (268435430u, 28), (268435431u, 28), (268435432u, 28), (16777194u, 24), (1073741820u, 30), (268435433u, 28),
        (268435434u, 28), (1073741821u, 30), (268435435u, 28), (268435436u, 28), (268435437u, 28), (268435438u, 28),
        (268435439u, 28), (268435440u, 28), (268435441u, 28), (268435442u, 28), (1073741822u, 30), (268435443u, 28),
        (268435444u, 28), (268435445u, 28), (268435446u, 28), (268435447u, 28), (268435448u, 28), (268435449u, 28),
        (268435450u, 28), (268435451u, 28), (20u, 6), (1016u, 10), (1017u, 10), (4090u, 12), (8185u, 13), (21u, 6),
        (248u, 8), (2042u, 11), (1018u, 10), (1019u, 10), (249u, 8), (2043u, 11), (250u, 8), (22u, 6), (23u, 6),
        (24u, 6), (0u, 5), (1u, 5), (2u, 5), (25u, 6), (26u, 6), (27u, 6), (28u, 6), (29u, 6), (30u, 6), (31u, 6),
        (92u, 7), (251u, 8), (32764u, 15), (32u, 6), (4091u, 12), (1020u, 10), (8186u, 13), (33u, 6), (93u, 7),
        (94u, 7), (95u, 7), (96u, 7), (97u, 7), (98u, 7), (99u, 7), (100u, 7), (101u, 7), (102u, 7), (103u, 7),
        (104u, 7), (105u, 7), (106u, 7), (107u, 7), (108u, 7), (109u, 7), (110u, 7), (111u, 7), (112u, 7), (113u, 7),
        (114u, 7), (252u, 8), (115u, 7), (253u, 8), (8187u, 13), (524272u, 19), (8188u, 13), (16380u, 14), (34u, 6),
        (32765u, 15), (3u, 5), (35u, 6), (4u, 5), (36u, 6), (5u, 5), (37u, 6), (38u, 6), (39u, 6), (6u, 5), (116u, 7),
        (117u, 7), (40u, 6), (41u, 6), (42u, 6), (7u, 5), (43u, 6), (118u, 7), (44u, 6), (8u, 5), (9u, 5), (45u, 6),
        (119u, 7), (120u, 7), (121u, 7), (122u, 7), (123u, 7), (32766u, 15), (2044u, 11), (16381u, 14), (8189u, 13),
        (268435452u, 28),
    ];

    private static readonly HuffmanNode HuffmanTreeRoot = BuildHuffmanTree();

    public static byte[] EncodeConnectHeaders(string authority, string proxyPassToken)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x03);
        WriteString(ms, "CONNECT");
        ms.WriteByte(0x01);
        WriteString(ms, authority);
        ms.WriteByte(0x0F);
        ms.WriteByte(0x22);
        WriteString(ms, "Bearer " + proxyPassToken);
        return ms.ToArray();
    }

    public static int DecodeStatusCode(ReadOnlySpan<byte> headerBlock, List<(string Name, string Value)>? dynamicTable = null)
    {
        var pos = 0;
        var resolvedStatus = 0;
        while (pos < headerBlock.Length)
        {
            var b = headerBlock[pos];
            if ((b & 0x80) != 0)
            {
                var idx = ReadInteger(headerBlock, ref pos, 7);
                switch (idx)
                {
                    case 8: resolvedStatus = 200; break;
                    case 9: resolvedStatus = 204; break;
                    case 10: resolvedStatus = 206; break;
                    case 11: resolvedStatus = 304; break;
                    case 12: resolvedStatus = 400; break;
                    case 13: resolvedStatus = 404; break;
                    case 14: resolvedStatus = 500; break;
                    default:
                        if (idx >= StaticTableNames.Length && dynamicTable is not null)
                        {
                            var dynIdx = idx - StaticTableNames.Length;
                            if (dynIdx < dynamicTable.Count)
                            {
                                var (dName, dVal) = dynamicTable[dynIdx];
                                if (string.Equals(dName, ":status", StringComparison.OrdinalIgnoreCase) &&
                                    int.TryParse(dVal, out var dStatus))
                                {
                                    resolvedStatus = dStatus;
                                }
                            }
                        }
                        break;
                }
            }
            else if ((b & 0x40) != 0)
            {
                var nameIndex = ReadInteger(headerBlock, ref pos, 6);
                var name = ResolveHeaderName(nameIndex, headerBlock, ref pos, dynamicTable);
                var value = ReadString(headerBlock, ref pos);
                if (dynamicTable is not null)
                {
                    dynamicTable.Insert(0, (name, value));
                    if (dynamicTable.Count > 128) dynamicTable.RemoveAt(dynamicTable.Count - 1);
                }
                if (string.Equals(name, ":status", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var status))
                    resolvedStatus = status;
            }
            else if ((b & 0xF0) is 0x00 or 0x10)
            {
                var nameIndex = ReadInteger(headerBlock, ref pos, 4);
                var name = ResolveHeaderName(nameIndex, headerBlock, ref pos, dynamicTable);
                var value = ReadString(headerBlock, ref pos);
                if (string.Equals(name, ":status", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var status))
                    resolvedStatus = status;
            }
            else if ((b & 0x20) != 0)
            {
                var maxSize = ReadInteger(headerBlock, ref pos, 5);
                if (maxSize == 0) dynamicTable?.Clear();
            }
            else
            {
                pos++;
            }
        }
        return resolvedStatus;
    }

    private static string ResolveHeaderName(
        int nameIndex,
        ReadOnlySpan<byte> headerBlock,
        ref int pos,
        List<(string Name, string Value)>? dynamicTable)
    {
        if (nameIndex == 0) return ReadString(headerBlock, ref pos);
        if (nameIndex < StaticTableNames.Length) return StaticTableNames[nameIndex];
        if (dynamicTable is not null)
        {
            var dynIdx = nameIndex - StaticTableNames.Length;
            if (dynIdx >= 0 && dynIdx < dynamicTable.Count) return dynamicTable[dynIdx].Name;
        }
        return "";
    }

    private static void WriteString(MemoryStream ms, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteInteger(ms, bytes.Length, 7, 0);
        ms.Write(bytes, 0, bytes.Length);
    }

    private static void WriteInteger(MemoryStream ms, int value, int prefixBits, byte prefixMask)
    {
        var maxPrefix = (1 << prefixBits) - 1;
        if (value < maxPrefix)
        {
            ms.WriteByte((byte)(prefixMask | value));
            return;
        }

        ms.WriteByte((byte)(prefixMask | maxPrefix));
        value -= maxPrefix;
        while (value >= 128)
        {
            ms.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        ms.WriteByte((byte)value);
    }

    private static int ReadInteger(ReadOnlySpan<byte> span, ref int pos, int prefixBits)
    {
        if (pos >= span.Length) return 0;
        var maxPrefix = (1 << prefixBits) - 1;
        var value = span[pos++] & maxPrefix;
        if (value < maxPrefix) return value;

        var shift = 0;
        while (pos < span.Length)
        {
            var b = span[pos++];
            value += (b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0) break;
        }
        return value;
    }

    private static string ReadString(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos >= span.Length) return "";
        var huffman = (span[pos] & 0x80) != 0;
        var length = ReadInteger(span, ref pos, 7);
        if (length <= 0 || pos + length > span.Length) return "";
        var bytes = span.Slice(pos, length);
        pos += length;
        return huffman ? DecodeHuffman(bytes) : Encoding.UTF8.GetString(bytes);
    }

    private static string DecodeHuffman(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        var node = HuffmanTreeRoot;
        foreach (var b in bytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                node = ((b >> bit) & 1) == 0 ? node.Left : node.Right;
                if (node is null) return sb.ToString();
                if (node.Symbol < 0) continue;
                if (node.Symbol == 256) return sb.ToString();
                sb.Append((char)node.Symbol);
                node = HuffmanTreeRoot;
            }
        }
        return sb.ToString();
    }

    private static HuffmanNode BuildHuffmanTree()
    {
        var root = new HuffmanNode();
        for (var symbol = 0; symbol < HuffmanCodeTable.Length; symbol++)
        {
            var (code, bits) = HuffmanCodeTable[symbol];
            var node = root;
            for (var bit = bits - 1; bit >= 0; bit--)
            {
                if (((code >> bit) & 1) == 0)
                {
                    node.Left ??= new HuffmanNode();
                    node = node.Left;
                }
                else
                {
                    node.Right ??= new HuffmanNode();
                    node = node.Right;
                }
            }
            node.Symbol = symbol;
        }
        return root;
    }
}
