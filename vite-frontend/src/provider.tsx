import type { ReactNode } from "react";
import { Toaster } from "react-hot-toast";

export function Provider({ children }: { children: ReactNode }) {
  return <>
    {children}
    <Toaster position="top-center" toastOptions={{ duration: 2200, style: { background: "#ffffff", color: "#29413b", border: "1px solid #dfe7e3", boxShadow: "0 12px 30px rgba(28, 59, 49, .12)", fontSize: "12px" } }} />
  </>;
}
