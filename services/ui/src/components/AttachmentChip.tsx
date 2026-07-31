import { HStack, IconButton, Image, Text } from "@chakra-ui/react";
import { FileAudio, X } from "lucide-react";
import { Attachment } from "../lib/api";

interface AttachmentChipProps {
  attachment: Attachment;
  /** Present for pending (not-yet-sent) attachments — renders a remove button. */
  onRemove?: () => void;
}

/** Small chip showing an image thumbnail or an audio icon — used both for
 * attachments pending in the compose box and for attachments already sent
 * on a rendered message. */
export function AttachmentChip({ attachment, onRemove }: AttachmentChipProps) {
  return (
    <HStack
      gap={1.5}
      bg="blackAlpha.100"
      borderRadius="md"
      px={2}
      py={1}
      fontSize="xs"
      data-testid="attachment-chip"
    >
      {attachment.kind === "image" ? (
        <Image src={attachment.url} alt="attachment" boxSize="24px" objectFit="cover" borderRadius="sm" />
      ) : (
        <FileAudio size={14} />
      )}
      <Text fontSize="xs" color="gray.600">
        {attachment.kind === "image" ? "Image" : "Voice message"}
      </Text>
      {onRemove && (
        <IconButton
          aria-label="Remove attachment"
          size="2xs"
          variant="ghost"
          onClick={onRemove}
        >
          <X size={12} />
        </IconButton>
      )}
    </HStack>
  );
}
