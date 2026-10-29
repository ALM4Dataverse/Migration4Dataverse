import { Text as FluentText } from "@fluentui/react-components";

type TextVariant = "small" | "smallPlus" | "mediumPlus" | "large" | "xxLarge";

const textSizes: Record<TextVariant, 200 | 300 | 400 | 500 | 900> = {
  small: 200,
  smallPlus: 300,
  mediumPlus: 400,
  large: 500,
  xxLarge: 900,
};

export function Text({
  variant = "mediumPlus",
  className,
  title,
  children,
}: {
  variant?: TextVariant;
  className?: string;
  title?: string;
  children: React.ReactNode;
}) {
  return (
    <FluentText
      className={["app-text", `text-${variant}`, className].filter(Boolean).join(" ")}
      size={textSizes[variant]}
      weight={variant === "large" || variant === "xxLarge" ? "semibold" : undefined}
      title={title}
    >
      {children}
    </FluentText>
  );
}
