import CoreGraphics
import ErdigoCore
import Foundation
import ImageIO

/// The photo checks integration-v1 requires of a receiver, in the order the specification gives them.
/// The result is a newly encoded image, so metadata such as EXIF location does not survive.
enum PhotoPipeline {
    /// Walks the structure of a JPEG and counts its scans. Returns nil for anything that is not
    /// well-formed, so that junk cannot hide a scan from this count but not from the decoder.
    static func jpegScanCount(_ data: Data) -> Int? {
        let bytes = [UInt8](data)
        var index = 2
        var scans = 0
        while index < bytes.count {
            guard bytes[index] == 0xFF else { return nil }
            index += 1
            // A marker may be preceded by any number of fill bytes.
            while index < bytes.count, bytes[index] == 0xFF { index += 1 }
            guard index < bytes.count else { return scans }
            let marker = bytes[index]
            index += 1
            switch marker {
            case 0xD9:
                return scans
            case 0xD8:
                // A second start of image. It has no length, and the decoder carries on after it.
                return nil
            case 0xD0...0xD7, 0x01:
                continue
            case 0x00:
                return nil
            default:
                guard index + 1 < bytes.count else { return scans }
                let length = Int(bytes[index]) << 8 | Int(bytes[index + 1])
                guard length >= 2 else { return nil }
                index += length
                guard marker == 0xDA else { continue }
                scans += 1
                // The coded data runs up to the next marker. FF 00 and restart markers are part of it.
                while index + 1 < bytes.count {
                    if bytes[index] == 0xFF {
                        var next = index + 1
                        while next < bytes.count, bytes[next] == 0xFF { next += 1 }
                        guard next < bytes.count else { return scans }
                        if bytes[next] != 0x00, !(0xD0...0xD7).contains(bytes[next]) { break }
                        index = next
                    }
                    index += 1
                }
                if index + 1 >= bytes.count { return scans }
            }
        }
        return scans
    }

    static let maxPixelSize = 4096
    /// Decoders expand a PNG in full before scaling it, so a tiny file can cost gigabytes without this.
    static let maxPixels = 64_000_000
    /// A progressive JPEG can repeat its scans so that a small file takes half a minute to decode.
    /// Ordinary encoders use about ten.
    static let maxJPEGScans = 32

    static func process(_ data: Data, declared: PhotoType) throws -> Data {
        // 1. First bytes must match the declared format.
        guard PhotoType.detect(data) == declared else {
            throw WireError(.photoInvalid, "the data is not the declared format")
        }

        // 2. The decoder must agree, without guessing from a hint.
        let options = [kCGImageSourceShouldCache: false] as CFDictionary
        guard let source = CGImageSourceCreateWithData(data as CFData, options),
              CGImageSourceGetCount(source) == 1,
              let actual = CGImageSourceGetType(source) as String?
        else {
            throw WireError(.photoInvalid, "the image could not be read")
        }
        let expected = declared == .jpeg ? "public.jpeg" : "public.png"
        guard actual == expected else { throw WireError(.photoInvalid, "the image format is not allowed") }

        // 3. Read the dimensions before decoding any pixels.
        guard let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, options) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0, width <= 32768, height <= 32768, width * height <= maxPixels
        else {
            throw WireError(.photoInvalid, "the image dimensions are not allowed")
        }

        if declared == .jpeg {
            guard let scans = jpegScanCount(data), scans <= maxJPEGScans else {
                throw WireError(.photoInvalid, "the image has too many scans or a damaged structure")
            }
        }

        // 4. Decode at no more than 4096 pixels on the long side.
        let decode = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
            kCGImageSourceShouldCacheImmediately: true,
        ] as CFDictionary
        guard let decoded = CGImageSourceCreateThumbnailAtIndex(source, 0, decode) else {
            throw WireError(.photoInvalid, "the image could not be decoded")
        }

        // 5. Draw into a new image with a fixed colour profile, because an embedded profile
        // is bytes chosen by the sender and would otherwise be copied into the result.
        let alpha: CGImageAlphaInfo = declared == .jpeg ? .noneSkipLast : .premultipliedLast
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(
                  data: nil,
                  width: decoded.width,
                  height: decoded.height,
                  bitsPerComponent: 8,
                  bytesPerRow: 0,
                  space: colorSpace,
                  bitmapInfo: alpha.rawValue
              )
        else {
            throw WireError(.internalError, "the image could not be encoded")
        }
        context.draw(decoded, in: CGRect(x: 0, y: 0, width: decoded.width, height: decoded.height))
        guard let image = context.makeImage() else {
            throw WireError(.internalError, "the image could not be encoded")
        }

        // 6. Encode again, with no metadata carried over.
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, expected as CFString, 1, nil) else {
            throw WireError(.internalError, "the image could not be encoded")
        }
        let encode = (declared == .jpeg ? [kCGImageDestinationLossyCompressionQuality: 0.85] : [:]) as CFDictionary
        CGImageDestinationAddImage(destination, image, encode)
        guard CGImageDestinationFinalize(destination) else {
            throw WireError(.internalError, "the image could not be encoded")
        }
        return output as Data
    }
}
