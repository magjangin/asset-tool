using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace asset_tool
{
    public sealed class SkelSkin
    {
        public string Name = "";
        // Slots this skin fills, in file order.
        public List<string> Slots = new();
    }

    public sealed class SkelSkinInfo
    {
        public string Version = "";
        // Skins other than "default". Editors show only the default skin until one
        // of these is picked, so a slot that is filled only here looks empty.
        public List<SkelSkin> ExtraSkins = new();
        // Slots that have an attachment in some extra skin but none in the default skin.
        public List<string> SlotsOnlyInExtraSkins = new();
    }

    // Reads just enough of a Spine 3.8 binary skeleton (.skel) to list its skins.
    // Muse Dash ships skeletons whose body parts live only in a named skin
    // ("origin" / "hexie" ...); the editor shows them empty until that skin is picked.
    // Other versions are not supported: the layout changed between releases.
    public static class SkelSkinReader
    {
        public static SkelSkinInfo? TryRead(string path)
        {
            try { return Read(File.ReadAllBytes(path)); }
            catch (Exception) { return null; }
        }

        public static SkelSkinInfo? Read(byte[] data)
        {
            var r = new Reader(data);
            r.String(); // hash
            var version = r.String() ?? "";
            if (!version.StartsWith("3.8.")) return null;

            r.Skip(16); // x, y, width, height
            bool nonessential = r.Byte() != 0;
            if (nonessential) { r.Skip(4); r.String(); r.String(); } // fps, images path, audio path

            int stringCount = r.VarInt();
            var strings = new string?[stringCount];
            for (int i = 0; i < stringCount; i++) strings[i] = r.String();
            string? Ref() { int i = r.VarInt(); return i == 0 ? null : strings[i - 1]; }

            int boneCount = r.VarInt();
            for (int i = 0; i < boneCount; i++)
            {
                r.String();
                if (i > 0) r.VarInt(); // parent
                r.Skip(32); // rotation, x, y, scaleX, scaleY, shearX, shearY, length
                r.VarInt(); // transform mode
                r.Byte();   // skin required
                if (nonessential) r.Skip(4);
            }

            int slotCount = r.VarInt();
            var slots = new string[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                slots[i] = r.String() ?? "";
                r.VarInt(); r.Skip(8); Ref(); r.VarInt(); // bone, color + dark color, attachment, blend
            }

            int ik = r.VarInt();
            for (int i = 0; i < ik; i++)
            {
                r.String(); r.VarInt(); r.Byte();
                SkipVarInts(r, r.VarInt());
                r.VarInt(); r.Skip(8); r.Skip(4); // target, mix + softness, bend / compress / stretch / uniform
            }
            int transform = r.VarInt();
            for (int i = 0; i < transform; i++)
            {
                r.String(); r.VarInt(); r.Byte();
                SkipVarInts(r, r.VarInt());
                r.VarInt(); r.Skip(2); r.Skip(40); // target, local + relative, 10 floats
            }
            int pathConstraints = r.VarInt();
            for (int i = 0; i < pathConstraints; i++)
            {
                r.String(); r.VarInt(); r.Byte();
                SkipVarInts(r, r.VarInt());
                r.VarInt(); r.VarInt(); r.VarInt(); r.VarInt(); r.Skip(20);
            }

            var info = new SkelSkinInfo { Version = version };
            var defaultSlots = ReadSkinSlots(r, slots, nonessential, Ref);

            int skinCount = r.VarInt();
            var extraSlots = new HashSet<string>();
            for (int i = 0; i < skinCount; i++)
            {
                var skin = new SkelSkin { Name = Ref() ?? "" };
                SkipVarInts(r, r.VarInt()); // bones
                SkipVarInts(r, r.VarInt() + r.VarInt() + r.VarInt()); // ik / transform / path constraints
                skin.Slots = ReadSkinSlots(r, slots, nonessential, Ref);
                info.ExtraSkins.Add(skin);
                foreach (var s in skin.Slots) extraSlots.Add(s);
            }

            var inDefault = new HashSet<string>(defaultSlots);
            info.SlotsOnlyInExtraSkins = extraSlots.Where(s => !inDefault.Contains(s)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            return info;
        }

        private static void SkipVarInts(Reader r, int count)
        {
            for (int i = 0; i < count; i++) r.VarInt();
        }

        // Returns the names of the slots that have at least one attachment.
        private static List<string> ReadSkinSlots(Reader r, string[] slots, bool nonessential, Func<string?> readRef)
        {
            var result = new List<string>();
            int slotCount = r.VarInt();
            for (int i = 0; i < slotCount; i++)
            {
                int slotIndex = r.VarInt();
                int attachmentCount = r.VarInt();
                for (int a = 0; a < attachmentCount; a++)
                {
                    readRef(); readRef(); // placeholder name, name
                    int type = r.Byte();
                    switch (type)
                    {
                        case 0: // region
                            readRef(); r.Skip(32);
                            break;
                        case 1: // bounding box
                            SkipVertices(r, r.VarInt());
                            if (nonessential) r.Skip(4);
                            break;
                        case 2: // mesh
                        {
                            readRef(); r.Skip(4);
                            int vertexCount = r.VarInt();
                            r.Skip(vertexCount * 8);          // uvs
                            r.Skip(r.VarInt() * 2);           // triangles
                            SkipVertices(r, vertexCount);
                            r.VarInt();                       // hull length
                            if (nonessential) { r.Skip(r.VarInt() * 2); r.Skip(8); }
                            break;
                        }
                        case 3: // linked mesh
                            readRef(); r.Skip(4); readRef(); readRef(); r.Byte();
                            if (nonessential) r.Skip(8);
                            break;
                        case 4: // path
                        {
                            r.Byte(); r.Byte();
                            int vertexCount = r.VarInt();
                            SkipVertices(r, vertexCount);
                            r.Skip(vertexCount / 3 * 4);
                            if (nonessential) r.Skip(4);
                            break;
                        }
                        case 5: // point
                            r.Skip(12);
                            if (nonessential) r.Skip(4);
                            break;
                        case 6: // clipping
                            r.VarInt();
                            SkipVertices(r, r.VarInt());
                            if (nonessential) r.Skip(4);
                            break;
                        default:
                            throw new InvalidDataException($"attachment type {type}");
                    }
                }
                if (attachmentCount > 0 && slotIndex >= 0 && slotIndex < slots.Length)
                    result.Add(slots[slotIndex]);
            }
            return result;
        }

        private static void SkipVertices(Reader r, int vertexCount)
        {
            if (r.Byte() == 0) { r.Skip(vertexCount * 8); return; }
            for (int i = 0; i < vertexCount; i++)
            {
                int bones = r.VarInt();
                for (int b = 0; b < bones; b++) { r.VarInt(); r.Skip(12); }
            }
        }

        private sealed class Reader
        {
            private readonly byte[] _d;
            private int _p;
            public Reader(byte[] d) { _d = d; }

            public byte Byte()
            {
                if (_p >= _d.Length) throw new EndOfStreamException();
                return _d[_p++];
            }

            public void Skip(int n)
            {
                if (n < 0 || _p + n > _d.Length) throw new EndOfStreamException();
                _p += n;
            }

            public int VarInt()
            {
                int result = 0;
                for (int shift = 0; shift < 35; shift += 7)
                {
                    byte b = Byte();
                    result |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) return result;
                }
                throw new InvalidDataException("varint");
            }

            // Length-prefixed UTF-8; 0 is null, 1 is empty.
            public string? String()
            {
                int n = VarInt();
                if (n == 0) return null;
                if (n == 1) return "";
                if (_p + n - 1 > _d.Length) throw new EndOfStreamException();
                var s = Encoding.UTF8.GetString(_d, _p, n - 1);
                _p += n - 1;
                return s;
            }
        }
    }
}
