import { Button, Dialog, HStack, Image } from "@chakra-ui/react";
import { X } from "lucide-react";
import { Attachment } from "../lib/api";

interface AttachmentViewerModalProps {
  attachment: Attachment | null;
  onClose: () => void;
}

/** Full-size image viewer / audio player, opened by clicking an
 * AttachmentChip — either a pending (already-uploaded) attachment or one on
 * a previously-sent message (including ones reloaded from a persisted
 * conversation). */
export function AttachmentViewerModal({ attachment, onClose }: AttachmentViewerModalProps) {
  return (
    <Dialog.Root open={attachment !== null} onOpenChange={(e) => !e.open && onClose()} size="lg">
      <Dialog.Backdrop />
      <Dialog.Positioner>
        <Dialog.Content borderRadius="2xl" data-testid="attachment-viewer-modal">
          <Dialog.Header borderBottomWidth="1px" borderColor="gray.100" px={6} py={3}>
            <HStack justify="flex-end" w="full">
              <Dialog.CloseTrigger asChild>
                <Button size="xs" variant="ghost" colorPalette="gray" borderRadius="md" px={2} h="26px">
                  <X size={12} />
                </Button>
              </Dialog.CloseTrigger>
            </HStack>
          </Dialog.Header>
          <Dialog.Body py={8} display="flex" justifyContent="center" alignItems="center">
            {attachment?.kind === "image" ? (
              <Image
                src={attachment.url}
                alt="attachment"
                maxH="70vh"
                maxW="100%"
                objectFit="contain"
                borderRadius="lg"
              />
            ) : attachment?.kind === "audio" ? (
              <audio controls autoPlay src={attachment.url} style={{ width: "100%" }} />
            ) : null}
          </Dialog.Body>
        </Dialog.Content>
      </Dialog.Positioner>
    </Dialog.Root>
  );
}
