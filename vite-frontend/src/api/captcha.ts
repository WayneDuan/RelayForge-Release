import Network from "./network";

export const checkCaptcha = () => Network.post("/captcha/check");
export const generateCaptcha = () => Network.post("/captcha/generate");
export const verifyCaptcha = (data: { captchaId: string; trackData: string }) => Network.post("/captcha/verify", data);
