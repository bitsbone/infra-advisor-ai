import React, { useEffect } from "react";
import { Flex, Spinner } from "@chakra-ui/react";
import { Chat } from "./components/Chat";
import { LoginPage } from "./components/LoginPage";
import { AuthProvider, useAuth } from "./hooks/useAuth";
import { EnvironmentBanner } from "./components/EnvironmentBanner";
import { initDatadogRum } from "./lib/datadog-rum";

function AppInner() {
  const { user, loading } = useAuth();

  let content: React.ReactNode;
  if (loading) {
    content = (
      <Flex flex="1" minH={0} align="center" justify="center" bg="gray.50">
        <Spinner size="lg" color="blue.500" />
      </Flex>
    );
  } else if (!user) {
    content = <LoginPage />;
  } else {
    content = <Chat />;
  }

  return (
    <Flex direction="column" h="100dvh" overflow="hidden">
      <EnvironmentBanner />
      <Flex direction="column" flex="1" minH={0} overflow="hidden">
        {content}
      </Flex>
    </Flex>
  );
}

export default function App() {
  useEffect(() => {
    initDatadogRum();
  }, []);

  return (
    <AuthProvider>
      <AppInner />
    </AuthProvider>
  );
}
