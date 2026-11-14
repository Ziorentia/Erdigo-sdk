// swift-tools-version: 5.9
import PackageDescription

// The Swift SDK lives under swift/. The manifest stays at the repository root so
// that Swift Package Manager can consume the repository URL directly.
let package = Package(
    name: "erdigo-sdk",
    platforms: [.macOS(.v12)],
    products: [
        .library(name: "ErdigoSDK", targets: ["ErdigoSDK"]),
        .library(name: "ErdigoMockReceiver", targets: ["ErdigoMockReceiver"]),
        .executable(name: "erdigo-send", targets: ["erdigo-send"]),
    ],
    targets: [
        .target(name: "ErdigoCore", path: "swift/Sources/ErdigoCore"),
        .target(name: "ErdigoSDK", dependencies: ["ErdigoCore"], path: "swift/Sources/ErdigoSDK"),
        .target(name: "ErdigoMockReceiver", dependencies: ["ErdigoCore"], path: "swift/Sources/ErdigoMockReceiver"),
        .executableTarget(
            name: "erdigo-send",
            dependencies: ["ErdigoSDK", "ErdigoMockReceiver"],
            path: "swift/Sources/erdigo-send"
        ),
        .executableTarget(name: "erdigo-vectors", dependencies: ["ErdigoCore"], path: "swift/Tools/erdigo-vectors"),
        .executableTarget(name: "erdigo-example-quickstart", dependencies: ["ErdigoSDK"], path: "swift/Examples/quickstart"),
        .testTarget(
            name: "ErdigoTests",
            dependencies: ["ErdigoCore", "ErdigoSDK", "ErdigoMockReceiver", "erdigo-send"],
            path: "swift/Tests/ErdigoTests"
        ),
    ]
)
