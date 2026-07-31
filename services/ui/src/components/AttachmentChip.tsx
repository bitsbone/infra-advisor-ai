import { HStack, IconButton, Image, Spinner, Text } from "@chakra-ui/react";
import { FileAudio, RotateCw, X } from "lucide-react";

interface AttachmentChipProps {
  kind: "image" | "audio";
  /** Local object URL (immediate preview) or the final Blob Storage URL. */
  previewUrl?: string;
  status?: "uploading" | "done" | "error";
  errorMessage?: string;
  onRemove?: () => void;
  onRetry?: () => void;
}

/** Small chip showing an image thumbnail or an audio icon — used both for
 * attachments pending in the compose box (with upload status) and for
 * attachments already sent on a rendered message (status omitted = "done"). */
export function AttachmentChip({ kind, previewUrl, status = "done", errorMessage, onRemove, onRetry }: AttachmentChipProps) {
  return (
    <HStack
      gap={1.5}
      bg={status === "error" ? "red.50" : "blackAlpha.100"}
      borderRadius="md"
      px={2}
      py={1}
      fontSize="xs"
      data-testid="attachment-chip"
      data-status={status}
    >
      {status === "uploading" ? (
        <Spinner size="xs" />
      ) : kind === "image" && previewUrl ? (
        <Image src={previewUrl} alt="attachment" boxSize="24px" objectFit="cover" borderRadius="sm" />
      ) : (
        <FileAudio size={14} />
      )}
      <Text fontSize="xs" color={status === "error" ? "red.600" : "gray.600"}>
        {status === "uploading"
          ? `Uploading ${kind === "image" ? "image" : "voice message"}…`
          : status === "error"
            ? (errorMessage ? `Failed: ${errorMessage}` : "Upload failed")
            : kind === "image" ? "Image" : "Voice message"}
      </Text>
      {status === "error" && onRetry && (
        <IconButton aria-label="Retry upload" size="2xs" variant="ghost" onClick={onRetry}>
          <RotateCw size={12} />
        </IconButton>
      )}
      {onRemove && (
        <IconButton aria-label="Remove attachment" size="2xs" variant="ghost" onClick={onRemove}>
          <X size={12} />
        </IconButton>
      )}
    </HStack>
  );
}
