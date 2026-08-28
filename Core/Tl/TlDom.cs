using System;
using System.Collections.Generic;

namespace Lumigram.Tl
{
    /// <summary>
    /// A parsed TL object: its constructor id plus its fields, addressed by name.
    ///
    /// Values are the obvious CLR types - int, long, double, string, byte[] - with
    /// nested objects as <see cref="TlObject"/> and vectors as List&lt;object&gt;.
    /// A field whose flag bit was clear is present with a null value, so asking for
    /// it is not an error.
    /// </summary>
    public sealed class TlObject
    {
        public uint Ctor;
        public string[] Names;
        public object[] Values;

        public object this[string name]
        {
            get
            {
                for (int i = 0; i < Names.Length; i++)
                    if (Names[i] == name) return Values[i];
                throw new TlParseException("0x" + Ctor.ToString("x8") + " has no field '" + name + "'");
            }
        }

        public bool Has(string name)
        {
            for (int i = 0; i < Names.Length; i++)
                if (Names[i] == name) return Values[i] != null;
            return false;
        }

        public int Int(string name) { return (int)this[name]; }
        public long Long(string name) { return (long)this[name]; }
        public string Str(string name) { return (string)this[name]; }
        public byte[] Bytes(string name) { return (byte[])this[name]; }
        public TlObject Obj(string name) { return (TlObject)this[name]; }

        public int IntOr(string name, int fallback)
        {
            object v = this[name];
            return v == null ? fallback : (int)v;
        }

        /// <summary>
        /// A double field, or a fallback when it is absent.
        ///
        /// Coordinates are the only doubles this client reads, and a missing one
        /// must not become a position off the coast of Africa.
        /// </summary>
        public double DoubleOr(string name, double fallback)
        {
            object v = this[name];
            return v == null ? fallback : (double)v;
        }

        public List<object> Vec(string name)
        {
            object v = this[name];
            return v == null ? new List<object>() : (List<object>)v;
        }

        /// <summary>True if the given bit of the named flags word is set.</summary>
        public bool Flag(string flagsField, int bit)
        {
            object v = this[flagsField];
            return v != null && (((int)v) & (1 << bit)) != 0;
        }

        public override string ToString()
        {
            return "0x" + Ctor.ToString("x8") + " (" + Names.Length + " fields)";
        }
    }

    /// <summary>
    /// Reads any TL object using the generated schema table.
    ///
    /// This exists so the client does not need a hand-written parser per type.
    /// message#7600b9d3 alone reaches a dozen further types through its optional
    /// fields; transcribing those by hand is both large and quietly error-prone,
    /// because a misread field does not fail - it returns the wrong value.
    ///
    /// The interpreter also makes *skipping* possible, which is what unlocks reading
    /// a whole vector of messages: TL elements carry no length, so reaching element
    /// two requires having consumed element one exactly.
    /// </summary>
    public static class TlSchema
    {
        private static Dictionary<uint, Entry> _table;
        private static readonly object _lock = new object();

        private sealed class Entry
        {
            public string[] Names;
            public string[] Specs;
        }

        private static Dictionary<uint, Entry> Table
        {
            get
            {
                if (_table != null) return _table;
                lock (_lock)
                {
                    if (_table == null) _table = Parse(TlSchemaData.Packed);
                    return _table;
                }
            }
        }

        private static Dictionary<uint, Entry> Parse(string packed)
        {
            var table = new Dictionary<uint, Entry>(4096);

            foreach (string entry in packed.Split(';'))
            {
                if (entry.Length < 9) continue;

                uint ctor = Convert.ToUInt32(entry.Substring(0, 8), 16);
                string body = entry.Substring(9);

                if (body.Length == 0)
                {
                    table[ctor] = new Entry { Names = new string[0], Specs = new string[0] };
                    continue;
                }

                string[] fields = body.Split(',');
                var names = new string[fields.Length];
                var specs = new string[fields.Length];

                for (int i = 0; i < fields.Length; i++)
                {
                    int eq = fields[i].IndexOf('=');
                    names[i] = fields[i].Substring(0, eq);
                    specs[i] = fields[i].Substring(eq + 1);
                }

                table[ctor] = new Entry { Names = names, Specs = specs };
            }

            return table;
        }

        public static bool IsKnown(uint ctor) { return Table.ContainsKey(ctor); }

        /// <summary>Reads a constructor id and the object that follows it.</summary>
        public static TlObject ReadObject(TlReader r)
        {
            uint ctor = r.ReadConstructor();
            return ReadBody(r, ctor);
        }

        public static TlObject ReadBody(TlReader r, uint ctor)
        {
            if (ctor == TlConstructors.Vector)
                throw new TlParseException(
                    "a bare Vector appeared where an object was expected; " +
                    "the schema does not say what its elements are");

            Entry e;
            if (!Table.TryGetValue(ctor, out e))
                throw new TlParseException("unknown constructor 0x" + ctor.ToString("x8") +
                                           " (schema is layer " + TlSchemaData.Layer + ")");

            var values = new object[e.Specs.Length];
            var flags = new List<int>(2);

            for (int i = 0; i < e.Specs.Length; i++)
            {
                string spec = e.Specs[i];

                int q = spec.IndexOf('?');
                if (q >= 0)
                {
                    // "N.B?rest"
                    int dot = spec.IndexOf('.');
                    int word = int.Parse(spec.Substring(0, dot));
                    int bit = int.Parse(spec.Substring(dot + 1, q - dot - 1));

                    if (word >= flags.Count)
                        throw new TlParseException("condition references flags word " + word +
                                                   " before it was read");

                    if ((flags[word] & (1 << bit)) == 0)
                    {
                        values[i] = null;
                        continue;
                    }
                    spec = spec.Substring(q + 1);
                }

                values[i] = ReadValue(r, spec, flags);
            }

            return new TlObject { Ctor = ctor, Names = e.Names, Values = values };
        }

        private static object ReadValue(TlReader r, string spec, List<int> flags)
        {
            switch (spec[0])
            {
                case '#':
                {
                    int v = r.ReadInt();
                    flags.Add(v);
                    return v;
                }
                case 'i': return r.ReadInt();
                case 'l': return r.ReadLong();
                case 'd': return r.ReadDouble();
                case 's': return r.ReadString();
                case 'b': return r.ReadBytes();
                case 'I': return r.ReadRaw(16);
                case 'J': return r.ReadRaw(32);
                case 'o': return ReadObject(r);

                case 'v':
                {
                    uint vec = r.ReadConstructor();
                    if (vec != TlConstructors.Vector)
                        throw new TlParseException("expected a vector, got 0x" + vec.ToString("x8"));
                    return ReadElements(r, spec.Substring(1), flags);
                }

                case 'V':
                    return ReadElements(r, spec.Substring(1), flags);

                default:
                    throw new TlParseException("unknown field spec '" + spec + "'");
            }
        }

        private static List<object> ReadElements(TlReader r, string elementSpec, List<int> flags)
        {
            int count = r.ReadInt();
            if (count < 0 || count > 1000000)
                throw new TlParseException("implausible vector count " + count);

            var list = new List<object>(count < 64 ? count : 64);
            for (int i = 0; i < count; i++) list.Add(ReadValue(r, elementSpec, flags));
            return list;
        }
    }
}
