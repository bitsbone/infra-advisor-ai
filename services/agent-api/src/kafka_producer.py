"""Fire-and-forget event publishing for downstream analysis/modeling.

Separate from kafka_consumer.py, which owns the synthetic-load eval loop's
own consumer/producer lifecycle and topics. This module is a lightweight,
lazily-initialized producer for one-off event publishes from the request
path — it must never raise into a chat request, and never blocks on
network I/O (no synchronous flush()).
"""

import json
import logging
import os
import time
from typing import Any

from confluent_kafka import Producer

logger = logging.getLogger(__name__)

KAFKA_BOOTSTRAP = os.environ.get(
    "KAFKA_BOOTSTRAP_SERVERS", "kafka-cluster-kafka-bootstrap.kafka.svc.cluster.local:9092"
)
TOPIC_CONTRACT_AWARDS_RAW = "infra.contract-awards.raw"

_producer: Producer | None = None


def _get_producer() -> Producer:
    global _producer
    if _producer is None:
        _producer = Producer({"bootstrap.servers": KAFKA_BOOTSTRAP})
    return _producer


def _delivery_callback(err, msg) -> None:
    if err is not None:
        logger.warning("Kafka delivery failed for topic=%s: %s", msg.topic() if msg else "?", err)


def publish_contract_awards_event(
    *,
    session_id: str,
    tool_call_id: str | None,
    query_input: dict[str, Any],
    raw_awards: list[dict[str, Any]],
    deduped_award_count: int,
) -> None:
    """Publish a contract_awards tool result for downstream analysis/modeling.

    Fire-and-forget: any failure (broker unreachable, serialization error) is
    logged and swallowed, never raised — this must never affect the chat
    response path.
    """
    try:
        payload = {
            "event_type": "contract_awards.query",
            "schema_version": "1.0",
            "occurred_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "session_id": session_id,
            "tool_call_id": tool_call_id,
            "query_input": query_input,
            "raw_awards": raw_awards,
            "raw_award_count": len(raw_awards),
            "deduped_award_count": deduped_award_count,
        }
        producer = _get_producer()
        producer.produce(
            TOPIC_CONTRACT_AWARDS_RAW,
            key=session_id.encode(),
            value=json.dumps(payload).encode(),
            callback=_delivery_callback,
        )
        producer.poll(0)
    except Exception as exc:
        logger.warning("Failed to publish contract_awards event: %s", exc)
