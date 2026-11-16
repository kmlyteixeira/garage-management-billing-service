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
    selector { match_labels = { app = "billing-service" } }
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
          readiness_probe {
            http_get {
              path = "/health"
              port = 8080
            }
          }
          liveness_probe {
            http_get {
              path = "/health"
              port = 8080
            }
            initial_delay_seconds = 15
            period_seconds        = 20
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
