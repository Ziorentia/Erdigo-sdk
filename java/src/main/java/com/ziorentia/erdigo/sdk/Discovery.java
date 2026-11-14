package com.ziorentia.erdigo.sdk;

import java.io.IOException;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.SocketException;
import java.net.StandardProtocolFamily;
import java.net.StandardSocketOptions;
import java.nio.ByteBuffer;
import java.nio.channels.DatagramChannel;
import java.nio.channels.SelectionKey;
import java.nio.channels.Selector;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Finds receivers that advertise {@code _erdigo-int._tcp}, with one-shot multicast DNS queries (RFC 6762 section 5.1)
 * sent from an ordinary port over IPv4. Receivers answer such a query directly, so nothing here needs port 5353, and
 * nothing here is used when an address is given.
 */
final class Discovery {
    private static final SecureRandom RANDOM = new SecureRandom();

    private Discovery() {
    }

    /** The address given or remembered, which can be tried without looking around the network. */
    static List<ReceiverEndpoint> knownEndpoints(ReceiverTarget target, ReceiverPairing pairing) {
        if (target instanceof ReceiverTarget.Address address) {
            return List.of(new ReceiverEndpoint(address.host(), address.port()));
        }
        if (pairing != null && pairing.lastHost() != null && pairing.lastPort() != null && pairing.lastPort() > 0) {
            return List.of(new ReceiverEndpoint(pairing.lastHost(), pairing.lastPort()));
        }
        return List.of();
    }

    /** Every endpoint to try when pairing, where no address is remembered. */
    static List<ReceiverEndpoint> candidates(ReceiverTarget target) {
        return target instanceof ReceiverTarget.Discover ? browse(2000, 8) : knownEndpoints(target, null);
    }

    /** Listens for receivers for a short time, and returns up to {@code limit} of them. */
    static List<ReceiverEndpoint> browse(long windowMillis, int limit) {
        DiscoveredServices found = new DiscoveredServices(limit);
        query(List.of(new Dns.Question(Dns.SERVICE_NAME, Dns.TYPE_PTR)), windowMillis, found);
        // Answers to the questions below only fill in what this first one found.
        found.closeInstances();
        if (found.instances().isEmpty()) {
            return List.of();
        }
        // A responder normally sends the service and its addresses along with the answer. Ask for whatever it did not.
        List<Dns.Question> services = new ArrayList<>();
        for (Dns.Name instance : found.instancesWithoutService()) {
            services.add(new Dns.Question(instance, Dns.TYPE_SRV));
        }
        if (!services.isEmpty()) {
            query(services, 1000, found);
        }
        List<Dns.Question> addresses = new ArrayList<>();
        for (Dns.Name host : found.hostsWithoutAddress()) {
            addresses.add(new Dns.Question(host, Dns.TYPE_A));
        }
        if (!addresses.isEmpty()) {
            query(addresses, 1000, found);
        }
        return found.endpoints();
    }

    /**
     * Sends one query on every IPv4 interface that can carry it, and hands {@code found} the records in the answers that
     * arrive within the window. Answers from beyond the local network are ignored.
     */
    private static void query(List<Dns.Question> questions, long windowMillis, DiscoveredServices found) {
        if (Thread.currentThread().isInterrupted()) {
            return;
        }
        int id = 1 + RANDOM.nextInt(65535);
        byte[] query = Dns.encodeQuery(id, questions);
        List<InterfaceAddress> local = new ArrayList<>();
        try (Selector selector = Selector.open()) {
            List<DatagramChannel> channels = openChannels(selector, query, local);
            try {
                if (channels.isEmpty()) {
                    return;
                }
                long start = System.nanoTime();
                long[] resendAt = {250, 750};
                int resent = 0;
                ByteBuffer buffer = ByteBuffer.allocate(9000);
                while (!Thread.currentThread().isInterrupted()) {
                    long elapsed = (System.nanoTime() - start) / 1_000_000;
                    if (elapsed >= windowMillis) {
                        break;
                    }
                    // Asked again a little later, like a browser that keeps watching, so that a receiver that has only
                    // just started advertising is found too.
                    if (resent < resendAt.length && elapsed >= resendAt[resent]) {
                        for (DatagramChannel channel : channels) {
                            send(channel, query);
                        }
                        resent += 1;
                        continue;
                    }
                    long next = resent < resendAt.length ? resendAt[resent] : windowMillis;
                    selector.select(Math.max(1, Math.min(next, windowMillis) - elapsed));
                    for (SelectionKey key : selector.selectedKeys()) {
                        DatagramChannel channel = (DatagramChannel) key.channel();
                        while (true) {
                            buffer.clear();
                            InetSocketAddress from = (InetSocketAddress) channel.receive(buffer);
                            if (from == null) {
                                break;
                            }
                            if (from.getPort() != Dns.MDNS_PORT || !isLocal(from.getAddress(), local)) {
                                continue;
                            }
                            Dns.Message message = Dns.decode(buffer.array(), buffer.position());
                            if (message == null || !message.isResponse() || message.id() != id) {
                                continue;
                            }
                            found.add(message.records());
                        }
                    }
                    selector.selectedKeys().clear();
                }
            } finally {
                for (DatagramChannel channel : channels) {
                    try {
                        channel.close();
                    } catch (IOException ignored) {
                        // Closing anyway.
                    }
                }
            }
        } catch (IOException error) {
            // Discovery finds what it can. A receiver it cannot find stays unfound, which is reported as no answer.
        }
    }

    private static void send(DatagramChannel channel, byte[] query) {
        try {
            channel.send(ByteBuffer.wrap(query), new InetSocketAddress(Dns.MULTICAST_V4, Dns.MDNS_PORT));
        } catch (IOException ignored) {
            // That interface cannot send just now.
        }
    }

    /** Opens one channel per IPv4 interface address, sends the query on each, and registers them with the selector. */
    private static List<DatagramChannel> openChannels(Selector selector, byte[] query, List<InterfaceAddress> local) {
        List<DatagramChannel> channels = new ArrayList<>();
        List<NetworkInterface> interfaces;
        try {
            interfaces = Collections.list(NetworkInterface.getNetworkInterfaces());
        } catch (SocketException | NullPointerException error) {
            return channels;
        }
        for (NetworkInterface networkInterface : interfaces) {
            try {
                if (!networkInterface.isUp() || networkInterface.isLoopback() || !networkInterface.supportsMulticast()) {
                    continue;
                }
            } catch (SocketException error) {
                continue;
            }
            for (InterfaceAddress interfaceAddress : networkInterface.getInterfaceAddresses()) {
                if (!(interfaceAddress.getAddress() instanceof Inet4Address address)) {
                    continue;
                }
                local.add(interfaceAddress);
                DatagramChannel channel = null;
                try {
                    channel = DatagramChannel.open(StandardProtocolFamily.INET);
                    channel.bind(new InetSocketAddress(address, 0));
                    channel.setOption(StandardSocketOptions.IP_MULTICAST_IF, networkInterface);
                    channel.setOption(StandardSocketOptions.IP_MULTICAST_TTL, 255);
                    channel.configureBlocking(false);
                    channel.send(ByteBuffer.wrap(query), new InetSocketAddress(Dns.MULTICAST_V4, Dns.MDNS_PORT));
                    channel.register(selector, SelectionKey.OP_READ);
                    channels.add(channel);
                    channel = null;
                } catch (IOException | RuntimeException error) {
                    // This interface cannot carry the query.
                } finally {
                    if (channel != null) {
                        try {
                            channel.close();
                        } catch (IOException ignored) {
                            // Closing anyway.
                        }
                    }
                }
            }
        }
        return channels;
    }

    /** Whether an answer came from this computer's own networks: an attached subnet, or a link-local address. */
    static boolean isLocal(InetAddress sender, List<InterfaceAddress> local) {
        if (!(sender instanceof Inet4Address)) {
            return false;
        }
        if (sender.isLinkLocalAddress()) {
            return true;
        }
        byte[] bytes = sender.getAddress();
        int value = ((bytes[0] & 0xFF) << 24) | ((bytes[1] & 0xFF) << 16) | ((bytes[2] & 0xFF) << 8) | (bytes[3] & 0xFF);
        for (InterfaceAddress interfaceAddress : local) {
            byte[] own = interfaceAddress.getAddress().getAddress();
            int ownValue = ((own[0] & 0xFF) << 24) | ((own[1] & 0xFF) << 16) | ((own[2] & 0xFF) << 8) | (own[3] & 0xFF);
            int prefix = interfaceAddress.getNetworkPrefixLength();
            if (prefix < 0 || prefix > 32) {
                continue;
            }
            int mask = prefix == 0 ? 0 : -1 << (32 - prefix);
            if ((value & mask) == (ownValue & mask)) {
                return true;
            }
        }
        return false;
    }

    /**
     * What the answers to one browse have said, kept to what the browse can use: at most {@code limit} instances, one
     * service for each, and a few addresses for each of their hosts, however many answers arrive.
     */
    static final class DiscoveredServices {
        static final int ADDRESSES_PER_HOST = 4;

        private final int limit;
        private final Map<String, Dns.Name> instances = new LinkedHashMap<>();
        private final Map<String, Dns.Record> services = new HashMap<>();
        private final Map<String, List<Inet4Address>> addresses = new HashMap<>();
        private boolean instancesClosed;

        DiscoveredServices(int limit) {
            this.limit = limit;
        }

        synchronized List<Dns.Name> instances() {
            return List.copyOf(instances.values());
        }

        /** How many records are kept, for checking that this stays bounded. */
        synchronized int keptRecords() {
            int count = instances.size() + services.size();
            for (List<Inet4Address> list : addresses.values()) {
                count += list.size();
            }
            return count;
        }

        synchronized void add(List<Dns.Record> records) {
            if (!instancesClosed) {
                for (Dns.Record record : records) {
                    if (instances.size() < limit && record.type() == Dns.TYPE_PTR && record.name().sameAs(Dns.SERVICE_NAME)
                            && record.target() != null && record.ttl() > 0) {
                        instances.putIfAbsent(record.target().key(), record.target());
                    }
                }
            }
            for (Dns.Record record : records) {
                // The first usable service for an instance stands, as a resolver would keep the first it heard.
                if (record.type() == Dns.TYPE_SRV && record.target() != null && record.port() != 0
                        && instances.containsKey(record.name().key()) && !services.containsKey(record.name().key())) {
                    services.put(record.name().key(), record);
                }
            }
            Map<String, Boolean> hosts = new HashMap<>();
            for (Dns.Record service : services.values()) {
                hosts.put(service.target().key(), true);
            }
            for (Dns.Record record : records) {
                if (record.type() != Dns.TYPE_A || record.address() == null || !hosts.containsKey(record.name().key())) {
                    continue;
                }
                List<Inet4Address> known = addresses.computeIfAbsent(record.name().key(), ignored -> new ArrayList<>());
                if (!known.contains(record.address()) && known.size() < ADDRESSES_PER_HOST) {
                    known.add(record.address());
                }
            }
        }

        /** From now on no new instances are taken, only what completes the ones already found. */
        synchronized void closeInstances() {
            instancesClosed = true;
        }

        synchronized List<Dns.Name> instancesWithoutService() {
            List<Dns.Name> missing = new ArrayList<>();
            for (Map.Entry<String, Dns.Name> instance : instances.entrySet()) {
                if (!services.containsKey(instance.getKey())) {
                    missing.add(instance.getValue());
                }
            }
            return missing;
        }

        synchronized List<Dns.Name> hostsWithoutAddress() {
            Map<String, Dns.Name> missing = new LinkedHashMap<>();
            for (Dns.Record service : services.values()) {
                if (!addresses.containsKey(service.target().key())) {
                    missing.putIfAbsent(service.target().key(), service.target());
                }
            }
            return new ArrayList<>(missing.values());
        }

        /** One address for each instance that has a service and an address, in the order the instances were found. */
        synchronized List<ReceiverEndpoint> endpoints() {
            List<ReceiverEndpoint> endpoints = new ArrayList<>();
            for (String instance : instances.keySet()) {
                Dns.Record service = services.get(instance);
                if (service == null) {
                    continue;
                }
                List<Inet4Address> known = addresses.get(service.target().key());
                if (known == null || known.isEmpty()) {
                    continue;
                }
                endpoints.add(new ReceiverEndpoint(known.get(0).getHostAddress(), service.port()));
            }
            return endpoints;
        }
    }
}
