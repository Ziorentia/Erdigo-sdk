package com.ziorentia.erdigo.sdk;

import java.io.ByteArrayOutputStream;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.UnknownHostException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/**
 * The small part of DNS that discovery needs: queries, and a bounds-checked reader for PTR, SRV and A records. Anything
 * malformed makes {@link #decode} return null; it never reads outside the message and never follows more name pointers
 * than a name can have.
 */
final class Dns {
    static final int MDNS_PORT = 5353;
    static final int TYPE_A = 1;
    static final int TYPE_PTR = 12;
    static final int TYPE_SRV = 33;
    static final int CLASS_IN = 1;
    static final InetAddress MULTICAST_V4 = multicast();

    /** The longest name DNS allows, in bytes on the wire, and so a bound on its labels too. */
    private static final int MAX_NAME_LENGTH = 255;
    /** Records read from one message. A real answer has a handful. */
    private static final int MAX_RECORDS = 256;

    private Dns() {
    }

    private static InetAddress multicast() {
        try {
            return InetAddress.getByAddress(new byte[] {(byte) 224, 0, 0, (byte) 251});
        } catch (UnknownHostException error) {
            throw new IllegalStateException(error);
        }
    }

    /** A name as its labels. Compared without regard to ASCII case, as DNS does. */
    record Name(List<String> labels) {
        Name {
            labels = List.copyOf(labels);
        }

        static Name of(String dotted) {
            List<String> labels = new ArrayList<>();
            for (String label : dotted.split("\\.")) {
                if (!label.isEmpty()) {
                    labels.add(label);
                }
            }
            return new Name(labels);
        }

        String key() {
            StringBuilder builder = new StringBuilder();
            for (String label : labels) {
                builder.append(label.toLowerCase(Locale.ROOT)).append('\0');
            }
            return builder.toString();
        }

        boolean sameAs(Name other) {
            return key().equals(other.key());
        }
    }

    /** The service browsed for. */
    static final Name SERVICE_NAME = Name.of("_erdigo-int._tcp.local");

    record Question(Name name, int type) {
    }

    /**
     * A record discovery may use. {@code target} is set for PTR and SRV, {@code port} for SRV and {@code address} for A.
     */
    record Record(Name name, int type, long ttl, Name target, int port, Inet4Address address) {
    }

    record Message(int id, boolean isResponse, List<Record> records) {
    }

    static byte[] encodeQuery(int id, List<Question> questions) {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        writeShort(output, id);
        writeShort(output, 0);
        writeShort(output, questions.size());
        writeShort(output, 0);
        writeShort(output, 0);
        writeShort(output, 0);
        for (Question question : questions) {
            for (String label : question.name().labels()) {
                byte[] bytes = label.getBytes(StandardCharsets.UTF_8);
                if (bytes.length == 0 || bytes.length > 63) {
                    throw new IllegalArgumentException("a DNS label is 1 to 63 bytes");
                }
                output.write(bytes.length);
                output.writeBytes(bytes);
            }
            output.write(0);
            writeShort(output, question.type());
            writeShort(output, CLASS_IN);
        }
        return output.toByteArray();
    }

    private static void writeShort(ByteArrayOutputStream output, int value) {
        output.write((value >> 8) & 0xFF);
        output.write(value & 0xFF);
    }

    /** Thrown inside the reader for anything malformed. */
    private static final class Malformed extends Exception {
        private static final long serialVersionUID = 1L;

        Malformed() {
            super(null, null, false, false);
        }
    }

    /** Reads a message, or returns null if it is malformed. Records of other types are skipped. */
    static Message decode(byte[] data, int length) {
        try {
            return new Reader(data, length).message();
        } catch (Malformed error) {
            return null;
        }
    }

    private static final class Reader {
        private final byte[] data;
        private final int length;
        private int offset;

        Reader(byte[] data, int length) {
            this.data = data;
            this.length = Math.min(length, data.length);
        }

        private int u8(int at) throws Malformed {
            if (at < 0 || at >= length) {
                throw new Malformed();
            }
            return data[at] & 0xFF;
        }

        private int u16() throws Malformed {
            int value = (u8(offset) << 8) | u8(offset + 1);
            offset += 2;
            return value;
        }

        private long u32() throws Malformed {
            long value = ((long) u16() << 16) | u16();
            return value;
        }

        Message message() throws Malformed {
            int id = u16();
            int flags = u16();
            int questions = u16();
            int answers = u16();
            int authorities = u16();
            int additional = u16();
            for (int index = 0; index < questions; index++) {
                name();
                offset += 4;
                if (offset > length) {
                    throw new Malformed();
                }
            }
            int total = answers + authorities + additional;
            if (total > MAX_RECORDS) {
                throw new Malformed();
            }
            List<Record> records = new ArrayList<>();
            for (int index = 0; index < total; index++) {
                Name name = name();
                int type = u16();
                u16();
                long ttl = u32();
                int dataLength = u16();
                int start = offset;
                int end = start + dataLength;
                if (end > length) {
                    throw new Malformed();
                }
                switch (type) {
                    case TYPE_PTR:
                        records.add(new Record(name, type, ttl, name(), 0, null));
                        break;
                    case TYPE_SRV: {
                        u16();
                        u16();
                        int port = u16();
                        records.add(new Record(name, type, ttl, name(), port, null));
                        break;
                    }
                    case TYPE_A:
                        if (dataLength != 4) {
                            throw new Malformed();
                        }
                        try {
                            byte[] address = {(byte) u8(start), (byte) u8(start + 1), (byte) u8(start + 2), (byte) u8(start + 3)};
                            records.add(new Record(name, type, ttl, null, 0, (Inet4Address) InetAddress.getByAddress(address)));
                        } catch (UnknownHostException error) {
                            throw new Malformed();
                        }
                        break;
                    default:
                        break;
                }
                if (offset > end) {
                    throw new Malformed();
                }
                offset = end;
            }
            return new Message(id, (flags & 0x8000) != 0, records);
        }

        /** Reads a name at the current offset, following compression pointers backwards only. */
        private Name name() throws Malformed {
            List<String> labels = new ArrayList<>();
            int at = offset;
            int resumeAt = -1;
            int wireLength = 0;
            int jumps = 0;
            while (true) {
                int size = u8(at);
                if ((size & 0xC0) == 0xC0) {
                    int pointer = ((size & 0x3F) << 8) | u8(at + 1);
                    // A pointer must go back, so a loop is impossible; the count bounds it all the same.
                    if (pointer >= at || ++jumps > MAX_NAME_LENGTH / 2) {
                        throw new Malformed();
                    }
                    if (resumeAt < 0) {
                        resumeAt = at + 2;
                    }
                    at = pointer;
                    continue;
                }
                if ((size & 0xC0) != 0) {
                    throw new Malformed();
                }
                if (size == 0) {
                    at += 1;
                    break;
                }
                wireLength += size + 1;
                if (wireLength > MAX_NAME_LENGTH || at + 1 + size > length) {
                    throw new Malformed();
                }
                labels.add(new String(data, at + 1, size, StandardCharsets.UTF_8));
                at += 1 + size;
            }
            offset = resumeAt >= 0 ? resumeAt : at;
            return new Name(labels);
        }
    }
}
