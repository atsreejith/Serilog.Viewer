import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./index.css";
import App from "./App";
import logoIcon from "./log-viewer-icon.png";

// Set favicon to the project icon
const faviconLink = document.querySelector(
  "link[rel~='icon']",
) as HTMLLinkElement | null;
if (faviconLink) {
  faviconLink.href = logoIcon;
  faviconLink.type = "image/png";
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
