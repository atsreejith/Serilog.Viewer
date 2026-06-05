import { createBrowserRouter, Navigate } from "react-router-dom";
import { MainLayout } from "@/layouts/MainLayout";
import { Dashboard } from "@/pages/Dashboard";
import { LogExplorer } from "@/pages/LogExplorer";
import { LiveTail } from "@/pages/LiveTail";

export const router = createBrowserRouter(
  [
    {
      path: "/",
      element: <MainLayout />,
      children: [
        { index: true, element: <Navigate to="/dashboard" replace /> },
        { path: "dashboard", element: <Dashboard /> },
        { path: "explorer", element: <LogExplorer /> },
        { path: "live", element: <LiveTail /> },
        { path: "*", element: <Navigate to="/dashboard" replace /> },
      ],
    },
  ],
  { basename: "/logviewer" },
);
