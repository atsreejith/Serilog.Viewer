import { useQuery } from "@tanstack/react-query";
import {
  AreaChart,
  Area,
  BarChart,
  Bar,
  PieChart,
  Pie,
  Cell,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  Legend,
} from "recharts";
import { format } from "date-fns";
import {
  AlertCircle,
  AlertTriangle,
  Skull,
  Activity,
  Database,
  FileText,
} from "lucide-react";
import { api } from "@/services/api";
import type { DashboardStats } from "@/types";
import { LOG_LEVEL_HEX, LOG_LEVEL_CONFIG } from "@/constants/logLevels";

function StatCard({
  label,
  value,
  icon: Icon,
  color,
}: {
  label: string;
  value: string | number;
  icon: React.ElementType;
  color: string;
}) {
  return (
    <div className="bg-[#161b22] border border-[#30363d] rounded-xl p-5 flex items-center gap-4">
      <div className={`p-2.5 rounded-lg ${color}`}>
        <Icon size={20} />
      </div>
      <div>
        <p className="text-2xl font-bold text-[#e6edf3] tabular-nums">
          {typeof value === "number" ? value.toLocaleString() : value}
        </p>
        <p className="text-xs text-[#8b949e] mt-0.5">{label}</p>
      </div>
    </div>
  );
}

export function Dashboard() {
  const { data: stats, isLoading } = useQuery<DashboardStats>({
    queryKey: ["dashboard-stats"],
    queryFn: () => api.getStats(),
    refetchInterval: 60_000,
  });

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-full text-[#484f58] text-sm animate-pulse">
        Loading dashboard…
      </div>
    );
  }

  if (!stats) return null;

  const errorHourData = stats.errorsByHour.map((p) => ({
    time: format(new Date(p.timestamp), "HH:mm"),
    errors: p.count,
  }));

  const dailyData = stats.logsByDay.map((p) => ({
    date: format(new Date(p.timestamp), "MMM dd"),
    logs: p.count,
  }));

  return (
    <div className="flex-1 overflow-y-auto p-6 space-y-6">
      {/* Stat cards */}
      <div className="grid grid-cols-2 lg:grid-cols-3 xl:grid-cols-5 gap-4">
        <StatCard
          label="Total Logs"
          value={stats.totalLogs}
          icon={Database}
          color={`${LOG_LEVEL_CONFIG.Debug.bg} ${LOG_LEVEL_CONFIG.Debug.color}`}
        />
        <StatCard
          label="Errors"
          value={stats.errors}
          icon={AlertCircle}
          color={`${LOG_LEVEL_CONFIG.Error.bg} ${LOG_LEVEL_CONFIG.Error.color}`}
        />
        <StatCard
          label="Warnings"
          value={stats.warnings}
          icon={AlertTriangle}
          color={`${LOG_LEVEL_CONFIG.Warning.bg} ${LOG_LEVEL_CONFIG.Warning.color}`}
        />
        <StatCard
          label="Fatal"
          value={stats.fatals}
          icon={Skull}
          color={`${LOG_LEVEL_CONFIG.Fatal.bg} ${LOG_LEVEL_CONFIG.Fatal.color}`}
        />
        <StatCard
          label="Active Files"
          value={stats.activeFiles}
          icon={Activity}
          color="bg-[#3fb950]/10 text-[#3fb950]"
        />
      </div>

      {/* Charts row 1 */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {/* Errors by hour */}
        <div className="bg-[#161b22] border border-[#30363d] rounded-xl p-5">
          <h3 className="text-sm font-semibold text-[#e6edf3] mb-4">
            Errors by Hour
          </h3>
          <ResponsiveContainer width="100%" height={200}>
            <AreaChart data={errorHourData}>
              <CartesianGrid strokeDasharray="3 3" stroke="#21262d" />
              <XAxis dataKey="time" tick={{ fill: "#484f58", fontSize: 11 }} />
              <YAxis tick={{ fill: "#484f58", fontSize: 11 }} />
              <Tooltip
                contentStyle={{
                  background: "#161b22",
                  border: "1px solid #30363d",
                  borderRadius: 8,
                }}
                labelStyle={{ color: "#8b949e" }}
                itemStyle={{ color: LOG_LEVEL_HEX.Error }}
              />
              <Area
                type="monotone"
                dataKey="errors"
                stroke={LOG_LEVEL_HEX.Error}
                fill={LOG_LEVEL_HEX.Error}
                fillOpacity={0.15}
                strokeWidth={2}
              />
            </AreaChart>
          </ResponsiveContainer>
        </div>

        {/* Daily volume */}
        <div className="bg-[#161b22] border border-[#30363d] rounded-xl p-5">
          <h3 className="text-sm font-semibold text-[#e6edf3] mb-4">
            Daily Volume
          </h3>
          <ResponsiveContainer width="100%" height={200}>
            <BarChart data={dailyData}>
              <CartesianGrid strokeDasharray="3 3" stroke="#21262d" />
              <XAxis dataKey="date" tick={{ fill: "#484f58", fontSize: 11 }} />
              <YAxis tick={{ fill: "#484f58", fontSize: 11 }} />
              <Tooltip
                contentStyle={{
                  background: "#161b22",
                  border: "1px solid #30363d",
                  borderRadius: 8,
                }}
                labelStyle={{ color: "#8b949e" }}
                itemStyle={{ color: LOG_LEVEL_HEX.Debug }}
              />
              <Bar
                dataKey="logs"
                fill={LOG_LEVEL_HEX.Debug}
                fillOpacity={0.8}
                radius={[3, 3, 0, 0]}
              />
            </BarChart>
          </ResponsiveContainer>
        </div>
      </div>

      {/* Charts row 2 */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {/* Logs by level */}
        <div className="bg-[#161b22] border border-[#30363d] rounded-xl p-5">
          <h3 className="text-sm font-semibold text-[#e6edf3] mb-4">
            Logs by Level
          </h3>
          <ResponsiveContainer width="100%" height={220}>
            <PieChart>
              <Pie
                data={stats.logsByLevel.filter((l) => l.count > 0)}
                dataKey="count"
                nameKey="level"
                cx="50%"
                cy="45%"
                outerRadius={70}
              >
                {stats.logsByLevel
                  .filter((l) => l.count > 0)
                  .map((entry) => (
                    <Cell
                      key={entry.level}
                      fill={
                        LOG_LEVEL_HEX[
                          entry.level as keyof typeof LOG_LEVEL_HEX
                        ] ?? entry.color
                      }
                    />
                  ))}
              </Pie>
              <Tooltip
                contentStyle={{
                  background: "#161b22",
                  border: "1px solid #30363d",
                  borderRadius: 8,
                }}
                labelStyle={{ color: "#8b949e" }}
                itemStyle={{ color: "#e6edf3" }}
                formatter={(value: number, name: string) => [
                  value.toLocaleString(),
                  name,
                ]}
              />
              <Legend
                wrapperStyle={{ fontSize: 11, color: "#8b949e" }}
                formatter={(value) => value}
              />
            </PieChart>
          </ResponsiveContainer>
        </div>

        {/* Top sources */}
        <div className="bg-[#161b22] border border-[#30363d] rounded-xl p-5">
          <h3 className="text-sm font-semibold text-[#e6edf3] mb-4">
            Top Sources
          </h3>
          <div className="space-y-2">
            {stats.topSources.slice(0, 8).map((src, i) => {
              const max = stats.topSources[0]?.count ?? 1;
              const pct = Math.round((src.count / max) * 100);
              return (
                <div key={src.source} className="flex items-center gap-2">
                  <span className="w-4 text-xs text-[#484f58] text-right">
                    {i + 1}
                  </span>
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center justify-between text-xs mb-0.5">
                      <span className="truncate font-mono text-[#8b949e]">
                        {src.source}
                      </span>
                      <span className="shrink-0 ml-2 text-[#484f58]">
                        {src.count.toLocaleString()}
                      </span>
                    </div>
                    <div className="h-1 bg-[#21262d] rounded-full">
                      <div
                        className="h-full bg-[#58a6ff] rounded-full"
                        style={{ width: `${pct}%` }}
                      />
                    </div>
                  </div>
                </div>
              );
            })}
            {stats.topSources.length === 0 && (
              <p className="text-[#484f58] text-xs">
                No source context data available.
              </p>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
