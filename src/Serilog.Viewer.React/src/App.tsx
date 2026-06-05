import { useEffect } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "react-router-dom";
import { router } from "./routes";
import { useAppStore } from "@/store/appStore";
import { api } from "@/services/api";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 10_000,
      retry: 1,
    },
  },
});

function AppInner() {
  const setLiveTailEnabled = useAppStore((s) => s.setLiveTailEnabled);

  useEffect(() => {
    api
      .getConfig()
      .then((cfg) => setLiveTailEnabled(cfg.liveTailEnabled))
      .catch(() => setLiveTailEnabled(false));
  }, [setLiveTailEnabled]);

  return <RouterProvider router={router} />;
}

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <AppInner />
    </QueryClientProvider>
  );
}
