package com.ziorentia.erdigo.core;

import java.util.List;

/**
 * A request from a device to a receiver. Decoding is an allow-list: unknown types and unknown fields are rejected, and
 * nothing about identity is ever taken from the message.
 */
public sealed interface Request {
    /** Checks the connection. */
    record Ping() implements Request {
    }

    record PutPin(ItemId id, Coordinate at, String title) implements Request {
    }

    record PutMarker(ItemId id, Coordinate at, String title, MarkerColor color) implements Request {
    }

    record PutNote(ItemId id, Coordinate at, String text) implements Request {
    }

    record Move(ItemId id, Coordinate to) implements Request {
    }

    record Delete(ItemId id) implements Request {
    }

    record DeleteAll() implements Request {
    }

    record PhotoBegin(PhotoHeader header) implements Request {
    }

    record PhotoChunk(ItemId id, int index, byte[] data) implements Request {
        public PhotoChunk {
            data = data.clone();
        }

        @Override
        public byte[] data() {
            return data.clone();
        }

        @Override
        public boolean equals(Object other) {
            return other instanceof PhotoChunk chunk && chunk.id.equals(id) && chunk.index == index
                    && java.util.Arrays.equals(chunk.data, data);
        }

        @Override
        public int hashCode() {
            return id.hashCode() * 31 + index;
        }

        @Override
        public String toString() {
            return "PhotoChunk[id=" + id + ", index=" + index + ", bytes=" + data.length + "]";
        }
    }

    record PhotoEnd(ItemId id) implements Request {
    }

    record TrackAppend(ItemId id, String title, List<TrackPoint> points) implements Request {
        public TrackAppend {
            points = List.copyOf(points);
        }
    }

    record TrackFinish(ItemId id) implements Request {
    }

    /** The header of a photo upload. */
    record PhotoHeader(ItemId id, Coordinate at, String title, PhotoType type, int size, String sha256, int chunks) {
    }
}
