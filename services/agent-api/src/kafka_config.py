"""Shared Kafka switch for the consumer thread and the event producer.

KAFKA_ENABLED=false turns off every Kafka touchpoint in this service. The dev
namespace sets it: prod and dev share one Kafka cluster, and a dev pod in the
same consumer group would take partitions (and synthetic queries) from prod.
Read at call time, not import time, so tests can toggle it.
"""

import os


def kafka_enabled() -> bool:
    return os.environ.get("KAFKA_ENABLED", "true").strip().lower() not in {"false", "0", "no", "off"}
