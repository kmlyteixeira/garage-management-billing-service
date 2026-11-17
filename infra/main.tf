locals {
  name = "${var.project_name}-${var.environment}-billing"
}

data "aws_ssm_parameter" "connection_string" {
  name            = "/${var.project_name}/${var.environment}/database/billing-connection-string"
  with_decryption = true
}

resource "kubernetes_secret_v1" "database" {
  metadata {
    name      = "billing-database"
    namespace = "garage-microservices"
  }
  data = { "connection-string" = data.aws_ssm_parameter.connection_string.value }
  type = "Opaque"
}

resource "kubernetes_deployment_v1" "service" {
  metadata {
    name      = "billing-service"
    namespace = "garage-microservices"
    labels    = { app = "billing-service" }
  }
  spec {
    replicas = var.replicas
    progress_deadline_seconds = 300
    selector { match_labels = { app = "billing-service" } }
    strategy {
      type = "RollingUpdate"
      rolling_update {
        max_surge       = "1"
        max_unavailable = "1"
      }
    }
    template {
      metadata { labels = { app = "billing-service" } }
      spec {
        container {
          name  = "billing-service"
          image = var.container_image
          port { container_port = 8080 }
          env {
            name = "ConnectionStrings__Billing"
            value_from {
              secret_key_ref {
                name = kubernetes_secret_v1.database.metadata[0].name
                key  = "connection-string"
              }
            }
          }
          env {
            name  = "RabbitMq__Uri"
            value = "amqp://rabbitmq:5672/"
          }
          env {
            name = "MERCADOPAGO_ACCESS_TOKEN"
            value_from {
              secret_key_ref {
                name     = "garage-payment"
                key      = "mercadopago-access-token"
                optional = true
              }
            }
          }
          env {
            name = "OTEL_EXPORTER_OTLP_ENDPOINT"
            value_from {
              secret_key_ref {
                name     = "garage-observability"
                key      = "otlp-endpoint"
                optional = true
              }
            }
          }
          env {
            name  = "ExternalJwt__Enabled"
            value = "false"
          }
          env {
            name = "ExternalJwt__Issuer"
            value_from {
              secret_key_ref {
                name     = "garage-auth"
                key      = "issuer"
                optional = true
              }
            }
          }
          env {
            name = "ExternalJwt__Audience"
            value_from {
              secret_key_ref {
                name     = "garage-auth"
                key      = "audience"
                optional = true
              }
            }
          }
          env {
            name = "ExternalJwt__PublicKeyPem"
            value_from {
              secret_key_ref {
                name     = "garage-auth"
                key      = "public-key-pem"
                optional = true
              }
            }
          }
          readiness_probe {
            http_get {
              path = "/health"
              port = 8080
            }
            initial_delay_seconds = 5
            period_seconds        = 5
            timeout_seconds       = 3
            failure_threshold     = 6
          }
          liveness_probe {
            http_get {
              path = "/health"
              port = 8080
            }
            initial_delay_seconds = 15
            period_seconds        = 20
            timeout_seconds       = 3
            failure_threshold     = 3
          }
        }
      }
    }
  }
}

resource "kubernetes_service_v1" "service" {
  metadata {
    name      = "billing-service"
    namespace = "garage-microservices"
  }
  spec {
    selector = { app = "billing-service" }
    port {
      port        = 8080
      target_port = 8080
    }
  }
}
