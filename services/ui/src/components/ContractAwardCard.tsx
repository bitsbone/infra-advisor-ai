import React from "react";
import {
  Badge,
  Box,
  Flex,
  Link,
  Text,
  VStack,
} from "@chakra-ui/react";
import { ContractAwardItem } from "../lib/api";

interface Props {
  award: ContractAwardItem;
}

function formatUsd(amount: number | null): string {
  if (amount === null) return "N/A";
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    maximumFractionDigits: 0,
  }).format(amount);
}

export function ContractAwardCard({ award }: Props) {
  return (
    <Box
      borderWidth="1px"
      borderColor="gray.200"
      borderRadius="lg"
      p={4}
      bg="gray.50"
      boxShadow="xs"
    >
      <VStack gap={2} align="stretch">
        <Flex justify="space-between" align="flex-start" gap={2}>
          <Box>
            <Text fontSize="sm" fontWeight="semibold" color="gray.800">
              {award.recipient_name}
            </Text>
            <Text fontSize="xs" fontFamily="mono" color="gray.500">
              {award.award_id}
            </Text>
          </Box>
          <Text fontSize="sm" fontWeight="semibold" color="green.700" whiteSpace="nowrap">
            {formatUsd(award.award_amount_usd)}
          </Text>
        </Flex>

        <Text fontSize="xs" color="gray.600">
          {award.awarding_agency}
          {award.awarding_sub_agency ? ` — ${award.awarding_sub_agency}` : ""}
        </Text>

        {award.description && (
          <Text fontSize="xs" color="gray.700">
            {award.description}
          </Text>
        )}

        <Flex flexWrap="wrap" gap={1.5} align="center">
          {award.contract_type && (
            <Badge colorPalette="blue" variant="subtle" fontSize="xs">
              {award.contract_type}
            </Badge>
          )}
          {award.naics_description && (
            <Badge colorPalette="gray" variant="subtle" fontSize="xs">
              {award.naics_description}
            </Badge>
          )}
        </Flex>

        <Flex justify="space-between" align="center">
          <Text fontSize="xs" color="gray.400">
            {award.place_of_performance}
            {award.start_date ? ` · ${award.start_date} – ${award.end_date ?? "?"}` : ""}
          </Text>
          {award.usaspending_permalink && (
            <Link
              href={award.usaspending_permalink}
              target="_blank"
              rel="noopener noreferrer"
              fontSize="xs"
              color="blue.600"
              whiteSpace="nowrap"
              _hover={{ textDecoration: "underline" }}
            >
              View on USASpending →
            </Link>
          )}
        </Flex>
      </VStack>
    </Box>
  );
}
