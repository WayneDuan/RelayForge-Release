import Network from "./network";

export const createSpeedLimit = (data: Record<string, unknown>) => Network.post("/speed-limit/create", data);
export const getSpeedLimitList = () => Network.post("/speed-limit/list");
export const updateSpeedLimit = (data: Record<string, unknown>) => Network.post("/speed-limit/update", data);
export const deleteSpeedLimit = (id: number) => Network.post("/speed-limit/delete", { id });

export const getConfigs = () => Network.post<Record<string, string>>("/config/list");
export const getConfigByName = (name: string) => Network.post<{ value?: string }>("/config/get", { name });
export const updateConfigs = (configMap: Record<string, string>) => Network.post("/config/update", configMap);
export const updateConfig = (name: string, value: string) => Network.post("/config/update-single", { name, value });

export interface TelegramSettingsData {
  enabled: boolean;
  botToken?: string;
  chatId: string;
  trafficThresholdPercent: number;
  notifyFlow: boolean;
  notifyNode: boolean;
}

export interface TelegramSettingsResponse {
  enabled: boolean;
  botTokenConfigured: boolean;
  chatId: string;
  trafficThresholdPercent: number;
  notifyFlow: boolean;
  notifyNode: boolean;
}

export const getTelegramSettings = () => Network.post<TelegramSettingsResponse>("/notification/telegram/status");
export const saveTelegramSettings = (data: TelegramSettingsData) => Network.post("/notification/telegram/save", data);
export const testTelegramSettings = () => Network.post("/notification/telegram/test");
