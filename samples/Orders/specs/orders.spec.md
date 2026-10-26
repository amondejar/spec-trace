---
spec: ORD
title: Gestión de pedidos
---
# Gestión de pedidos

Spec de ejemplo para SpecTrace. Cada requisito lleva un ID, un enunciado en formato EARS
y criterios de aceptación numerados.

## ORD-001: Crear pedido
Status: approved

When the customer submits a cart with at least one line,
the system shall create an order in state Pending with the same lines.

- AC-1: Given a cart with 2 lines, when it is submitted, then an order with 2 lines in state Pending is created.
- AC-2: Given an empty cart, when it is submitted, then the request is rejected with an error message.

## ORD-002: Cancelar pedido
Status: approved

While an order is in state Pending, when the customer cancels it,
the system shall move it to state Cancelled.

- AC-1: Given a Pending order, when it is cancelled, then its state is Cancelled.
- AC-2: Given a Shipped order, when it is cancelled, then the request is rejected.

## ORD-003: Aplicar cupón de descuento
Status: approved

When a valid coupon is applied to a Pending order,
the system shall reduce the order total by the coupon percentage, never below zero.

- AC-1: Given a 100 EUR order and a 10% coupon, when it is applied, then the total is 90 EUR.
- AC-2: Given an expired coupon, when it is applied, then the total does not change.

## ORD-004: Exportar pedidos a XML
Status: deprecated

Sustituido por la API de exportación JSON.

## ORD-005: Notificar al cliente
Status: draft

When an order changes state, the system shall notify the customer by email.

- AC-1: Given an order that is cancelled, when the state changes, then an email is sent.
