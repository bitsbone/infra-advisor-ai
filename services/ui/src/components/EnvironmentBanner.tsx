import { Flex, Text } from "@chakra-ui/react";
import { appEnv, appVersion } from "../lib/app-config";

/** Slim, non-dismissible banner shown on every non-prod environment. */
export function EnvironmentBanner() {
  const env = appEnv();
  if (env === "prod") return null;

  return (
    <Flex
      role="status"
      data-testid="environment-banner"
      h="28px"
      flexShrink={0}
      align="center"
      justify="center"
      px={3}
      bg="orange.600"
      color="white"
    >
      <Text fontSize="xs" fontWeight="semibold" letterSpacing="wide" lineClamp={1}>
        {env.toUpperCase()} environment · {appVersion()} · test data only
      </Text>
    </Flex>
  );
}
