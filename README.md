# Billing Service

Responsável por orçamento e pagamento. O PostgreSQL é exclusivo deste serviço.
O Mercado Pago é acessado somente por `IMercadoPagoGateway`; configure
`MERCADOPAGO_ACCESS_TOKEN` como Secret e nunca como valor de manifesto.

Endpoints: `POST /quotes`, `POST /quotes/{serviceOrderId}/approve`,
`POST /payments` e `GET /health`. Aprovação e pagamento publicam eventos
correlacionados no RabbitMQ; pagamentos rejeitados retornam `422`.
