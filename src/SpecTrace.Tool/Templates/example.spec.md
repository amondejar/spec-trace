---
spec: EX
title: Spec de ejemplo
---
# Spec de ejemplo

Cada requisito: encabezado `## ID: Título`, estado opcional, enunciado (EARS recomendado)
y criterios de aceptación `- AC-n: ...`. Estados: draft, proposed, approved (por defecto), implemented, deprecated.

## EX-001: Saludar al usuario
Status: proposed

When a user opens the home page, the system shall greet them by their first name.

- AC-1: Given a user named Ana, when she opens the home page, then she sees "Hola, Ana".
- AC-2: Given an anonymous visitor, when they open the home page, then they see "Hola".
