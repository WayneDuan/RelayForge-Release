import type { ReactNode } from "react";
import * as Dialog from "@radix-ui/react-dialog";

export function Modal({ title, children, onClose, className = "", open = true }: {
  title: string;
  children: ReactNode;
  onClose: () => void;
  className?: string;
  open?: boolean;
}) {
  return <Dialog.Root open={open} onOpenChange={(value) => { if (!value) onClose(); }}>
    <Dialog.Portal>
      <Dialog.Overlay className="dialog-overlay" />
      <Dialog.Content className={`create-modal modern-dialog ${className}`} aria-describedby={undefined}>
        <Dialog.Title className="sr-only">{title}</Dialog.Title>
        {children}
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>;
}
