#!/usr/bin/env python
"""
Generates Core/Tl/TlSchema.g.cs from Telegram's TL schema.

Why generate: message#7600b9d3 alone has ~35 optional fields pulling in a dozen
further types. Hand-writing parsers for that tree is most of a client, and every
transcribed field is a chance to misread a value as something else. The schema
is the authority, so the parser comes from the schema.

What is emitted is not parser code but a compact *field table*: one entry per
constructor, describing its fields in order. A small interpreter in TlDom.cs
walks that table, which keeps the generated file to data rather than thousands
of lines of switch statements.

Field encoding, comma separated:

    #          a flags word (int32) - referenced by later conditions
    i l d      int32, int64, double
    s b        string, bytes
    I J        int128 (16 raw bytes), int256 (32 raw bytes)
    o          a boxed object: read a constructor id, then recurse
    v<spec>    a boxed vector of <spec>
    V<spec>    a bare vector (no constructor id) of <spec>
    N.B?<spec> present only if bit B of flags word N is set

Each field is written as `name=spec`, so callers look fields up by name rather
than by position. Positional access would silently read the wrong field the
moment a layer inserts one.

Fields of type `true` carry no data at all and are omitted entirely.

Usage:
    python generate-schema.py <path-to-telegram_api.tl> <output.cs> [layer]
"""

import re
import sys

PRIMITIVES = {
    'int': 'i',
    'long': 'l',
    'double': 'd',
    'string': 's',
    'bytes': 'b',
    'int128': 'I',
    'int256': 'J',
    '#': '#',
}


def encode_type(t):
    """Maps a TL type expression to the table encoding."""
    t = t.strip()

    if t in PRIMITIVES:
        return PRIMITIVES[t]

    # Vector<X> is boxed (carries its own constructor id); vector<X> is bare.
    m = re.match(r'^Vector<(.+)>$', t)
    if m:
        return 'v' + encode_type(m.group(1))
    m = re.match(r'^vector<(.+)>$', t)
    if m:
        return 'V' + encode_type(m.group(1))

    # %Type means "the bare form of Type"; treat as a boxed object, which is
    # what the few uses in this schema amount to for skipping purposes.
    if t.startswith('%'):
        return 'o'

    # !X and X are template placeholders - always a boxed object.
    if t.startswith('!') or t == 'X':
        return 'o'

    # Bool, and every other named type, is boxed.
    return 'o'


def parse_line(line, flag_words):
    """Turns one schema line into (ctor_id, name, spec) or None."""
    line = line.strip()
    if not line or line.startswith('//') or line.startswith('---'):
        return None
    if not line.endswith(';'):
        return None
    line = line[:-1]

    if '=' not in line:
        return None
    head, result = line.rsplit('=', 1)
    head = head.strip()

    m = re.match(r'^([A-Za-z0-9_.]+)#([0-9a-fA-F]+)\s*(.*)$', head)
    if not m:
        return None

    name, ctor_hex, rest = m.group(1), m.group(2), m.group(3)
    ctor = int(ctor_hex, 16)

    # Drop template parameter declarations like {X:Type}
    rest = re.sub(r'\{[^}]*\}', ' ', rest)

    fields = []
    flag_words.clear()

    for arg in rest.split():
        if ':' not in arg:
            continue
        fname, ftype = arg.split(':', 1)

        if ftype == '#':
            flag_words.append(fname)
            fields.append(fname + '=#')
            continue

        cond = ''
        m2 = re.match(r'^([A-Za-z0-9_]+)\.(\d+)\?(.+)$', ftype)
        if m2:
            word, bit, ftype = m2.group(1), int(m2.group(2)), m2.group(3)
            if word not in flag_words:
                # A condition on a flags word we have not seen: unparseable.
                return None
            cond = '%d.%d?' % (flag_words.index(word), bit)

        # `true` occupies no bytes - it exists only as the flag bit itself.
        if ftype == 'true':
            continue

        fields.append(fname + '=' + cond + encode_type(ftype))

    return ctor, name, ','.join(fields)


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    schema_path, out_path = sys.argv[1], sys.argv[2]
    layer = sys.argv[3] if len(sys.argv) > 3 else '0'

    entries = {}
    names = {}
    skipped = 0
    flag_words = []

    with open(schema_path, 'r', encoding='utf-8') as f:
        for line in f:
            parsed = parse_line(line, flag_words)
            if parsed is None:
                if line.strip() and not line.strip().startswith('//') \
                        and not line.strip().startswith('---'):
                    skipped += 1
                continue
            ctor, name, spec = parsed
            entries[ctor] = spec
            names[ctor] = name

    # One packed string beats thousands of array elements: it keeps the
    # generated file small and costs a single split at first use.
    packed = ';'.join('%08x:%s' % (c, entries[c]) for c in sorted(entries))

    chunks = [packed[i:i + 200] for i in range(0, len(packed), 200)]

    with open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('// <auto-generated>\n')
        f.write('//     Generated by Tools/generate-schema.py from Telegram\'s TL schema.\n')
        f.write('//     Layer %s. %d constructors.\n' % (layer, len(entries)))
        f.write('//\n')
        f.write('//     Do not edit. Re-run the generator against a newer schema instead.\n')
        f.write('//     The encoding is documented in the generator and in TlDom.cs.\n')
        f.write('// </auto-generated>\n\n')
        f.write('namespace Lumigram.Tl\n{\n')
        f.write('    internal static class TlSchemaData\n    {\n')
        f.write('        public const int Layer = %s;\n\n' % layer)
        f.write('        // "<ctor-hex>:<field-spec>" entries, semicolon separated.\n')
        f.write('        public static readonly string Packed =\n')
        for i, c in enumerate(chunks):
            esc = c.replace('\\', '\\\\').replace('"', '\\"')
            sep = ' +' if i < len(chunks) - 1 else ';'
            f.write('            "%s"%s\n' % (esc, sep))
        f.write('    }\n}\n')

    print('%d constructors written to %s' % (len(entries), out_path))
    if skipped:
        print('%d schema lines not parsed (comments/blank/unsupported)' % skipped)
    return 0


if __name__ == '__main__':
    sys.exit(main())
