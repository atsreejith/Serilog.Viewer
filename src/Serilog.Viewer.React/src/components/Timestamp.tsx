import { format, formatDistanceToNow } from "date-fns";

interface TimestampProps {
  value: string;
  relative?: boolean;
  className?: string;
}

export function Timestamp({
  value,
  relative = false,
  className = "",
}: TimestampProps) {
  const date = new Date(value);
  const formatted = format(date, "yyyy-MM-dd HH:mm:ss.SSS");
  const title = format(date, "yyyy-MM-dd'T'HH:mm:ss.SSSXXX");

  return (
    <time
      dateTime={title}
      title={title}
      className={`font-mono text-xs tabular-nums text-[#8b949e] ${className}`}
    >
      {relative ? formatDistanceToNow(date, { addSuffix: true }) : formatted}
    </time>
  );
}
