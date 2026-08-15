import type { ReactNode, SVGProps } from "react";

type IconProps = SVGProps<SVGSVGElement> & { size?: number };

const icon = (children: ReactNode, label: string) => function UiIcon({ size = 18, ...props }: IconProps) {
  return <svg aria-label={label} fill="none" height={size} viewBox="0 0 24 24" width={size} stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.8" {...props}>{children}</svg>;
};

export const Activity = icon(<><path d="M3 12h4l2-7 4 14 2-7h6" /><circle cx="12" cy="12" r="9" /></>, "activity");
export const ArrowDownToLine = icon(<><path d="M12 3v13" /><path d="m7 11 5 5 5-5" /><path d="M5 21h14" /></>, "arrow down");
export const ArrowDownToLineIcon = ArrowDownToLine;
export const ArrowUpFromLine = icon(<><path d="M12 21V8" /><path d="m7 13 5-5 5 5" /><path d="M5 3h14" /></>, "arrow up");
export const Bell = icon(<><path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9" /><path d="M10 21h4" /></>, "notifications");
export const Check = icon(<path d="m5 12 4 4L19 6" />, "check");
export const ChevronRight = icon(<path d="m9 18 6-6-6-6" />, "next");
export const CircleHelp = icon(<><circle cx="12" cy="12" r="9" /><path d="M9.7 9a2.4 2.4 0 1 1 4.3 1.5c-.9 1.1-2 1.1-2 2.5" /><path d="M12 17h.01" /></>, "help");
export const Gauge = icon(<><path d="M4 15a8 8 0 1 1 16 0" /><path d="m12 11 3-3" /><path d="M5 19h14" /></>, "gauge");
export const HardDrive = icon(<><rect x="3" y="5" width="18" height="14" rx="2" /><path d="M7 15h.01M11 15h.01" /><path d="M3 10h18" /></>, "server");
export const LayoutDashboard = icon(<><rect x="3" y="3" width="7" height="8" rx="1" /><rect x="14" y="3" width="7" height="5" rx="1" /><rect x="14" y="12" width="7" height="9" rx="1" /><rect x="3" y="15" width="7" height="6" rx="1" /></>, "dashboard");
export const LogOut = icon(<><path d="M10 17l5-5-5-5" /><path d="M15 12H3" /><path d="M21 19V5a2 2 0 0 0-2-2h-5" /></>, "log out");
export const Network = icon(<><rect x="9" y="3" width="6" height="5" rx="1" /><rect x="3" y="16" width="6" height="5" rx="1" /><rect x="15" y="16" width="6" height="5" rx="1" /><path d="M12 8v4M6 16v-2h12v2" /></>, "network");
export const Pause = icon(<><rect x="6" y="4" width="4" height="16" rx="1" /><rect x="14" y="4" width="4" height="16" rx="1" /></>, "pause");
export const Play = icon(<path d="m8 5 11 7-11 7V5z" />, "play");
export const Plus = icon(<><path d="M12 5v14M5 12h14" /></>, "add");
export const RefreshCw = icon(<><path d="M20 11a8.1 8.1 0 0 0-14-4L4 9" /><path d="M4 4v5h5" /><path d="M4 13a8.1 8.1 0 0 0 14 4l2-2" /><path d="M20 20v-5h-5" /></>, "refresh");
export const Router = icon(<><rect x="3" y="8" width="18" height="9" rx="2" /><path d="M7 12h.01M11 12h.01M15 12h.01M12 8V4M9 4h6" /></>, "router");
export const Search = icon(<><circle cx="10.8" cy="10.8" r="6.8" /><path d="m16 16 5 5" /></>, "search");
export const Settings2 = icon(<><path d="M4 6h8M16 6h4M4 12h2M10 12h10M4 18h8M16 18h4" /><circle cx="14" cy="6" r="2" /><circle cx="8" cy="12" r="2" /><circle cx="14" cy="18" r="2" /></>, "settings");
export const ShieldCheck = icon(<><path d="M12 3 20 6v5c0 5-3.4 8.2-8 10-4.6-1.8-8-5-8-10V6l8-3z" /><path d="m8 12 2.5 2.5L16 9" /></>, "secure");
export const SlidersHorizontal = icon(<><path d="M4 6h6M14 6h6M4 12h2M10 12h10M4 18h6M14 18h6" /><circle cx="12" cy="6" r="2" /><circle cx="8" cy="12" r="2" /><circle cx="12" cy="18" r="2" /></>, "sliders");
export const Terminal = icon(<><path d="m5 7 4 5-4 5" /><path d="M12 17h7" /></>, "terminal");
export const UserRound = icon(<><circle cx="12" cy="8" r="4" /><path d="M4 21a8 8 0 0 1 16 0" /></>, "user");
export const X = icon(<><path d="m6 6 12 12M18 6 6 18" /></>, "close");
export const Zap = icon(<path d="m13 2-9 12h7l-1 8 9-12h-7l1-8z" />, "zap");
