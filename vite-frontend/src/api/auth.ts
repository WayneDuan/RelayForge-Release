import Network from "./network";

export interface LoginData {
  username: string;
  password: string;
  captchaId?: string;
  totpCode?: string;
}

export interface LoginResponse {
  token: string;
  role_id: number;
  name: string;
  requirePasswordChange?: boolean;
  requiresTotp?: boolean;
}

export interface PasswordUpdateData {
  newUsername?: string;
  currentPassword: string;
  newPassword: string;
  confirmPassword: string;
}

export const login = (data: LoginData) => Network.post<LoginResponse>("/user/login", data);
export const getAdminWebSocketTicket = () => Network.post<{ ticket: string }>("/user/ws-ticket");
export const updatePassword = (data: PasswordUpdateData) => Network.post("/user/updatePassword", data);
export const getTwoFactorStatus = () => Network.post<{ enabled: boolean }>("/user/2fa/status");
export const setupTwoFactor = () => Network.post<{ secret: string; otpauthUri: string }>("/user/2fa/setup");
export const enableTwoFactor = (data: { currentPassword: string; secret: string; code: string }) => Network.post("/user/2fa/enable", data);
export const disableTwoFactor = (data: { currentPassword: string; code: string }) => Network.post("/user/2fa/disable", data);
